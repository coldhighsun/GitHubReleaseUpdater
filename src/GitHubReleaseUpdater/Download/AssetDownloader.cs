using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Verification;

namespace GitHubReleaseUpdater.Download;

/// <summary>
/// Streams a release asset to disk with progress reporting and cancellation.
/// </summary>
public sealed class AssetDownloader
{
    /// <summary>
    /// Suffix appended to the in-progress download file before it is renamed to its final name.
    /// </summary>
    private const string PartialSuffix = ".partial";

    /// <summary>
    /// Suffix appended to the partial file's path for the sidecar file that records which asset (id and size) it
    /// belongs to, so a stale partial from a different asset/release is never resumed into, plus a checkpoint of
    /// how many of its bytes have actually been verified on disk.
    /// </summary>
    private const string MetaSuffix = ".meta";

    /// <summary>
    /// Number of trailing bytes hashed at each checkpoint (and re-hashed on resume) to detect a torn/corrupted
    /// write near the last recorded checkpoint without re-hashing the entire partial file.
    /// </summary>
    private const int TailHashWindow = 4096;

    /// <summary>
    /// Per-partial-file locks, keyed by full path, serializing concurrent <see cref="DownloadAsync"/> calls that
    /// target the same destination so one call's cleanup can never race another call's in-flight write to the
    /// same <c>.partial</c>/<c>.meta</c> pair. Entries are removed once their last holder releases them (see
    /// <see cref="RefCountedLock"/>) so this never grows unbounded across the lifetime of a long-running process
    /// that downloads to many distinct paths.
    /// </summary>
    private static readonly ConcurrentDictionary<string, RefCountedLock> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A <see cref="SemaphoreSlim"/> paired with a count of callers currently holding or waiting on it, so the
    /// owning <see cref="PathLocks"/> entry can be removed exactly when it becomes unused.
    /// </summary>
    private sealed class RefCountedLock
    {
        /// <summary>
        /// The underlying per-path lock.
        /// </summary>
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>
        /// Number of <see cref="DownloadAsync"/> calls currently holding or waiting to acquire <see cref="Semaphore"/>.
        /// Mutated only while holding the <see cref="PathLocks"/> dictionary's own lock.
        /// </summary>
        public int RefCount;
    }

    /// <summary>
    /// The GitHub client used to open the asset stream.
    /// </summary>
    private readonly IGitHubReleaseClient _client;

    /// <summary>
    /// Buffer size used when copying the response stream.
    /// </summary>
    public int BufferSize { get; init; } = 81920;

    /// <summary>
    /// Minimum interval between progress callbacks.
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Extra attempts made after a failed download before giving up, applied per <see cref="DownloadAsync"/> call.
    /// A transient failure (network I/O error, request timeout, a body that stalls past <see cref="IdleTimeout"/>,
    /// a truncated body or one whose size differs from the
    /// release's listed size, or a GitHub 5xx/429/408 response)
    /// is retried with exponential backoff starting at <see cref="RetryDelay"/> and doubling each attempt; a
    /// checksum mismatch, disk error, invalid argument, or caller cancellation is never retried. Default 2 (3
    /// attempts total).
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 2;

    /// <summary>
    /// Delay before the first retry; each subsequent retry doubles it. Set to <see cref="TimeSpan.Zero"/> to
    /// retry immediately.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When true (the default), a <c>.partial</c> file left on disk by an earlier attempt — whether from a prior
    /// retry within the same <see cref="DownloadAsync"/> call or a previous call that never got to clean up (e.g.
    /// the process was killed) — is resumed via an HTTP range request instead of re-downloaded from byte 0, once
    /// its identity and the checksum of its last checkpointed bytes both match the sidecar <c>.meta</c> file
    /// written alongside it. If the underlying <see cref="IGitHubReleaseClient"/> doesn't support range requests
    /// (its <see cref="IGitHubReleaseClient.OpenAssetStreamAsync(GitHub.Models.GitHubAsset, long, CancellationToken)"/>
    /// returns a non-partial stream) the download transparently restarts from 0 instead. When false, every attempt
    /// starts from 0 and any failure (other than a successful completion) deletes the partial file, matching this
    /// type's behavior before resume support was added. This setting is independent of <c>overwrite</c>, which only
    /// governs whether an already-completed file at the destination may be replaced.
    /// </summary>
    public bool AllowResume { get; init; } = true;

    /// <summary>
    /// Backing field of <see cref="IdleTimeout"/>.
    /// </summary>
    private readonly TimeSpan? _idleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest a single read of the asset stream may wait for data before the transfer is treated as stalled and
    /// fails with <see cref="TimeoutException"/>, which is retried (resuming from the last checkpoint when
    /// <see cref="AllowResume"/> is true) like any other transient failure. The timer restarts on every chunk
    /// received, so this bounds idle time, not total transfer time — without it a connection that stays open but
    /// stops sending data would hang the download forever. Null (or <see cref="Timeout.InfiniteTimeSpan"/>)
    /// disables it. Default 30 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is shorter than 1 millisecond or too large.</exception>
    public TimeSpan? IdleTimeout
    {
        get => _idleTimeout;
        init
        {
            TimeoutGuard.ThrowIfInvalid(value, nameof(IdleTimeout));
            _idleTimeout = value;
        }
    }

    /// <summary>
    /// Creates a downloader.
    /// </summary>
    public AssetDownloader(IGitHubReleaseClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="directory"/> (created if missing) and returns the final file path.
    /// Data is written to <c>&lt;name&gt;.partial</c> and atomically renamed on completion. When <see cref="AllowResume"/>
    /// is true (the default) a failed download leaves the partial file in place so a later attempt — another retry
    /// within this call, or an entirely new call after the process restarts — can resume it; only a caller-cancelled
    /// download always cleans it up. When <see cref="AllowResume"/> is false, any failure or cancellation leaves no
    /// partial file behind.
    /// </summary>
    /// <param name="asset">Asset to download.</param>
    /// <param name="directory">Destination directory.</param>
    /// <param name="fileName">Optional destination file name; defaults to the asset name.</param>
    /// <param name="overwrite">Whether to replace an existing completed file at the destination. Does not affect resume eligibility.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> DownloadAsync(
        GitHubAsset asset,
        string directory,
        string? fileName = null,
        bool overwrite = true,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (path, _) = await DownloadCoreAsync(asset, directory, fileName, overwrite, progress, computeSha256: false, expectedSha256: null, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Like <see cref="DownloadAsync"/> (replacing any existing destination file), but hashes the completed
    /// <c>.partial</c> before it is moved into place and returns that SHA-256 alongside the final path. When
    /// <paramref name="expectedSha256"/> is supplied and does not match, the partial and its sidecar are deleted,
    /// the destination is left untouched, and <see cref="ChecksumMismatchException"/> is thrown without retrying.
    /// </summary>
    internal async Task<(string Path, string Sha256)> DownloadVerifiedAsync(
        GitHubAsset asset,
        string directory,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (path, sha256) = await DownloadCoreAsync(asset, directory, fileName: null, overwrite: true, progress, computeSha256: true, expectedSha256, cancellationToken).ConfigureAwait(false);
        return (path, sha256!);
    }

    /// <summary>
    /// Shared implementation of <see cref="DownloadAsync"/> and <see cref="DownloadVerifiedAsync"/>. The returned
    /// hash is null unless <paramref name="computeSha256"/> is true.
    /// </summary>
    private async Task<(string Path, string? Sha256)> DownloadCoreAsync(
        GitHubAsset asset,
        string directory,
        string? fileName,
        bool overwrite,
        IProgress<DownloadProgress>? progress,
        bool computeSha256,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var name = SanitizeFileName(fileName ?? asset.Name);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, name);
        var partialPath = finalPath + PartialSuffix;
        var metaPath = partialPath + MetaSuffix;

        if (File.Exists(finalPath) && !overwrite)
        {
            throw new IOException($"File already exists: {finalPath}");
        }

        var pathKey = Path.GetFullPath(partialPath);
        RefCountedLock pathLock;
        lock (PathLocks)
        {
            pathLock = PathLocks.GetOrAdd(pathKey, static _ => new RefCountedLock());
            pathLock.RefCount++;
        }

        var lockAcquired = false;
        try
        {
            await pathLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockAcquired = true;

            // Whether the one extra restart a rejected range earns past MaxRetryAttempts has been spent, so a partial
            // that can't be deleted (and so keeps sending the same doomed Range request) can't loop forever.
            var rangeRestartUsed = false;
            for (var attempt = 0; ; attempt++)
            {
                // Whether this attempt sent a Range header, so a 416 for it can always fall back to a full restart.
                var requestedRange = false;
                try
                {
                    // Resume eligibility depends only on AllowResume and the partial's verified checkpoint, never
                    // on overwrite (which governs replacing the already-completed final file, an unrelated concern).
                    var verifiedLength = AllowResume ? PartialMatchesAsset(partialPath, metaPath, asset) : null;
                    var resumeFrom = verifiedLength ?? 0;
                    requestedRange = resumeFrom > 0;
                    await using var source = await _client.OpenAssetStreamAsync(asset, resumeFrom, cancellationToken).ConfigureAwait(false);
                    // The release's listed size is authoritative, so a body whose total differs is a different
                    // revision of the asset or a broken response; fail before touching the partial file instead of
                    // finalizing it.
                    if (asset.Size > 0 && source.TotalLength is { } serverTotal && serverTotal != asset.Size)
                    {
                        throw new UpdaterException($"Asset size mismatch: the release lists {asset.Size} bytes but the server reported {serverTotal}.");
                    }
                    var resuming = resumeFrom > 0 && source.IsPartial;
                    var initialReceived = resuming ? resumeFrom : 0L;
                    var total = source.TotalLength ?? (asset.Size > 0 ? asset.Size : null);

                    var target = resuming
                        ? new FileStream(partialPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan)
                        : new FileStream(partialPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

                    await using (target)
                    {
                        if (resuming)
                        {
                            // Discard any bytes beyond the last verified checkpoint (e.g. a torn write from a
                            // crash mid-flush) so only content that passed ComputeTailHash is ever built upon.
                            target.SetLength(resumeFrom);
                            target.Position = resumeFrom;
                        }
                        else
                        {
                            WriteAssetMarker(metaPath, asset);
                        }

                        var checkpoint = AllowResume ? (metaPath, asset) : ((string MetaPath, GitHubAsset Asset)?)null;
                        await CopyWithProgressAsync(source.Stream, target, initialReceived, total, progress, checkpoint, cancellationToken).ConfigureAwait(false);
                        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    string? sha256 = null;
                    if (computeSha256)
                    {
                        sha256 = await FileHasher.Sha256Async(partialPath, cancellationToken).ConfigureAwait(false);
                        if (expectedSha256 is not null && !FileHasher.HashEquals(expectedSha256, sha256))
                        {
                            // Verify before the move so a bad download never replaces a good existing file. The
                            // partial is complete but wrong, so resuming it could never help: discard it outright.
                            TryDelete(partialPath);
                            TryDelete(metaPath);
                            // Report the (now deleted) partial, never finalPath: that may hold a good earlier download
                            // a caller must not be led into deleting.
                            throw new ChecksumMismatchException(partialPath, expectedSha256, sha256);
                        }
                    }

                    // Re-check (rather than trust the pre-lock check above) so a concurrent DownloadAsync call for
                    // the same destination — which only just released pathLock after completing — can't be
                    // silently clobbered by this call finishing second.
                    if (File.Exists(finalPath) && !overwrite)
                    {
                        throw new IOException($"File already exists: {finalPath}");
                    }
                    // Replace in a single move so a failure can never leave the old file deleted without the new
                    // one in place.
                    File.Move(partialPath, finalPath, overwrite);
                    TryDelete(metaPath);
                    return (finalPath, sha256);
                }
                // A range that's no longer valid for this asset (e.g. it was regenerated with a smaller size between
                // attempts) can never succeed by resuming again, so the partial is discarded unconditionally — even
                // when AllowResume is true — so this and any later call restarts from 0 instead of failing forever.
                // A rejected range (e.g. a complete partial of an asset whose size is unknown, so PartialMatchesAsset
                // couldn't tell it was complete) earns one restart even past MaxRetryAttempts; the restart normally
                // sends no Range header, and rangeRestartUsed stops it looping when the partial couldn't be deleted.
                catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    TryDelete(partialPath);
                    TryDelete(metaPath);
                    if (attempt >= MaxRetryAttempts)
                    {
                        if (!requestedRange || rangeRestartUsed)
                        {
                            throw;
                        }
                        rangeRestartUsed = true;
                    }
                    await DelayBeforeRetryAsync(attempt, partialPath, metaPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MaxRetryAttempts && IsTransient(ex))
                {
                    if (!AllowResume)
                    {
                        TryDelete(partialPath);
                        TryDelete(metaPath);
                    }
                    await DelayBeforeRetryAsync(attempt, partialPath, metaPath, cancellationToken).ConfigureAwait(false);
                }
                // Only the caller's own cancellation discards the partial; any other OperationCanceledException (e.g.
                // HttpClient.CancelPendingRequests) is an ordinary failure below, so the partial stays resumable.
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryDelete(partialPath);
                    TryDelete(metaPath);
                    throw;
                }
                catch
                {
                    if (!AllowResume)
                    {
                        TryDelete(partialPath);
                        TryDelete(metaPath);
                    }
                    throw;
                }
            }
        }
        finally
        {
            // Only release a semaphore this call actually acquired — e.g. cancellation while still queued in
            // WaitAsync above must not release a lock this call never held.
            if (lockAcquired)
            {
                pathLock.Semaphore.Release();
            }
            lock (PathLocks)
            {
                if (--pathLock.RefCount == 0)
                {
                    PathLocks.TryRemove(pathKey, out _);
                }
            }
        }
    }

    /// <summary>
    /// Returns the number of verified, resumable bytes at the start of <paramref name="partialPath"/> when it and
    /// its sidecar <paramref name="metaPath"/> exist, the marker's identity matches <paramref name="asset"/> (id
    /// and size), the checkpointed tail-hash of those bytes still matches what's on disk, and the checkpoint stops
    /// short of the asset's listed size (a complete one has no bytes left to range-request). Returns null (and
    /// deletes any stale sidecar/partial) for a missing partial, a missing/malformed/mismatched marker, an orphaned
    /// marker whose partial file is gone, a checkpoint whose tail bytes no longer hash to the recorded value, or an
    /// I/O error reading either file (e.g. transiently locked by antivirus/backup software) — resume is a best-effort
    /// optimization, so any doubt about the partial's trustworthiness falls back to a full restart rather than
    /// letting the error abort the whole download.
    /// </summary>
    private static long? PartialMatchesAsset(string partialPath, string metaPath, GitHubAsset asset)
    {
        if (!File.Exists(partialPath))
        {
            TryDelete(metaPath);
            return null;
        }

        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            var stored = File.ReadAllText(metaPath);
            var lines = stored.Split('\n', 2);
            if (lines.Length != 2 || lines[0] != AssetMarker(asset))
            {
                TryDelete(metaPath);
                return null;
            }

            var parts = lines[1].Split(':', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var verifiedLength) || verifiedLength < 0)
            {
                TryDelete(metaPath);
                return null;
            }

            var actualLength = new FileInfo(partialPath).Length;
            if (verifiedLength > actualLength)
            {
                TryDelete(metaPath);
                return null;
            }

            // A checkpoint covering the whole asset (e.g. the final rename failed or the process died just before
            // it) leaves nothing to request: "Range: bytes={Size}-" is unsatisfiable and would only earn a 416, so
            // restart from 0 instead.
            if (asset.Size > 0 && verifiedLength >= asset.Size)
            {
                return null;
            }

            return ComputeTailHash(partialPath, verifiedLength) == parts[1] ? verifiedLength : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the sidecar <c>.meta</c> file recording which asset a freshly (re)started partial download belongs
    /// to, with an initial checkpoint of zero verified bytes.
    /// </summary>
    private static void WriteAssetMarker(string metaPath, GitHubAsset asset) =>
        WriteCheckpoint(metaPath, asset, 0, ComputeTailHash(ReadOnlySpan<byte>.Empty));

    /// <summary>
    /// Overwrites the sidecar <c>.meta</c> file with the asset's identity and a checkpoint recording that
    /// <paramref name="verifiedLength"/> bytes hashing to <paramref name="tailHashHex"/> are safe to resume from.
    /// Best-effort: an I/O error (e.g. a transient antivirus/backup lock) is silently ignored rather than aborting
    /// the download, since a missing or stale checkpoint only costs a future resume, never the current transfer.
    /// </summary>
    private static void WriteCheckpoint(string metaPath, GitHubAsset asset, long verifiedLength, string tailHashHex)
    {
        try
        {
            File.WriteAllText(metaPath, $"{AssetMarker(asset)}\n{verifiedLength}:{tailHashHex}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Flushes <paramref name="target"/>, then hashes its last <see cref="TailHashWindow"/> bytes and records the
    /// checkpoint via <see cref="WriteCheckpoint"/> as a best-effort unit. Only the hash-and-write half is guarded:
    /// a failed checkpoint only costs a future resume, never the current transfer, so an I/O error there is
    /// tolerable — but the flush writes the actual downloaded bytes, not checkpoint bookkeeping, so its errors
    /// (e.g. a full disk) are left to propagate and fail the transfer fast, same as before checkpointing existed.
    /// </summary>
    private static async Task TryCheckpointAsync(FileStream target, string metaPath, GitHubAsset asset, long verifiedLength, CancellationToken cancellationToken)
    {
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WriteCheckpoint(metaPath, asset, verifiedLength, ComputeTailHash(target, verifiedLength));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Identity used to detect a stale partial file: the download URL is unique per asset (and is required to be
    /// non-empty to download it at all — see <c>CreateAssetRequest</c>), unlike <see cref="GitHubAsset.Id"/> alone,
    /// which defaults to 0 for a <see cref="GitHubAsset"/> not populated from a real GitHub API response and would
    /// otherwise let two unrelated zero-id assets of the same size collide. Id and size are still included as a
    /// cheap extra guard.
    /// </summary>
    private static string AssetMarker(GitHubAsset asset)
    {
        var url = !string.IsNullOrEmpty(asset.ApiUrl) ? asset.ApiUrl : asset.BrowserDownloadUrl;
        return $"{url}:{asset.Id}:{asset.Size}";
    }

    /// <summary>
    /// Hashes the last <see cref="TailHashWindow"/> bytes (or fewer, if <paramref name="length"/> is smaller) of
    /// <paramref name="path"/> up to <paramref name="length"/>, used before <paramref name="path"/> is opened for
    /// writing (i.e. while validating a candidate resume).
    /// </summary>
    private static string ComputeTailHash(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, length - TailHashWindow);
        var buffer = new byte[length - start];
        stream.Position = start;
        stream.ReadExactly(buffer);
        return ComputeTailHash(buffer);
    }

    /// <summary>
    /// Hashes the last <see cref="TailHashWindow"/> bytes (or fewer, if <paramref name="length"/> is smaller)
    /// already written to <paramref name="target"/>, reading back through the same handle (which must have just
    /// been flushed) and restoring its position to <paramref name="length"/> afterwards — even if the read itself
    /// throws — so a caller that swallows the exception can safely keep writing to <paramref name="target"/>
    /// without silently continuing from the wrong offset.
    /// </summary>
    private static string ComputeTailHash(FileStream target, long length)
    {
        var start = Math.Max(0, length - TailHashWindow);
        var buffer = new byte[length - start];
        target.Position = start;
        try
        {
            target.ReadExactly(buffer);
        }
        finally
        {
            target.Position = length;
        }
        return ComputeTailHash(buffer);
    }

    /// <summary>
    /// Hex-encoded MD5 of <paramref name="bytes"/>. Used only to detect accidental corruption of a resumed
    /// partial file, not as a security control.
    /// </summary>
    private static string ComputeTailHash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    /// <summary>
    /// True for exceptions worth retrying: transport-level failures (<see cref="HttpRequestException"/> and
    /// <see cref="System.Net.Http.HttpIOException"/>, the latter thrown when the response body stream breaks
    /// mid-transfer, e.g. a reset connection), request timeouts, a truncated or wrongly sized body (an exact
    /// <see cref="UpdaterException"/>, not one of its subclasses such as <see cref="ChecksumMismatchException"/>),
    /// and a <see cref="GitHubApiException"/> carrying a transient GitHub status code (408, 429, or 5xx).
    /// Deliberately excludes plain <see cref="IOException"/> so local disk errors (full disk, locked file) writing
    /// the partial file fail fast instead of retrying a doomed operation. False for caller cancellation, argument
    /// errors, and every other library exception.
    /// </summary>
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TimeoutException or System.Net.Http.HttpIOException
        || ex.GetType() == typeof(UpdaterException)
        || ex is GitHubApiException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError };

    /// <summary>
    /// Waits out the backoff before retry <paramref name="attempt"/> + 1. Runs inside a <c>catch</c> block of the retry
    /// loop, whose sibling <c>catch (OperationCanceledException)</c> can't see an exception thrown from here — so a
    /// caller cancellation during the wait does its own cleanup, keeping the "caller cancellation always removes the
    /// partial file" contract of <see cref="DownloadAsync"/>.
    /// </summary>
    private async Task DelayBeforeRetryAsync(int attempt, string partialPath, string metaPath, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ExponentialDelay(RetryDelay, attempt), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            TryDelete(metaPath);
            throw;
        }
    }

    /// <summary>
    /// Longest delay <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts (<see cref="uint.MaxValue"/> - 1
    /// milliseconds, about 49.7 days); anything longer makes it throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    private static readonly TimeSpan MaxTaskDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Doubles <paramref name="baseDelay"/> per <paramref name="attempt"/>, clamped to <see cref="MaxTaskDelay"/> so a
    /// large <paramref name="attempt"/> and/or <paramref name="baseDelay"/> neither overflows <see cref="TimeSpan"/>
    /// nor exceeds what <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts.
    /// </summary>
    internal static TimeSpan ExponentialDelay(TimeSpan baseDelay, int attempt)
    {
        // A zero base stays zero; without this, 0 * 2^attempt is NaN once 2^attempt overflows to infinity.
        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        var ticks = baseDelay.Ticks * Math.Pow(2, attempt);
        return ticks >= MaxTaskDelay.Ticks ? MaxTaskDelay : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> in <see cref="BufferSize"/> chunks, reporting
    /// progress and (when <paramref name="checkpoint"/> is supplied) recording a resume checkpoint at most every
    /// <see cref="ProgressInterval"/> — except the very first chunk, which is always checkpointed immediately so a
    /// failure right after it still leaves a verified, resumable partial instead of waiting out a full interval —
    /// and throwing if the final received byte count (starting from <paramref name="initialReceived"/>, non-zero
    /// when resuming a partial download) falls short of <paramref name="total"/>.
    /// </summary>
    private async Task CopyWithProgressAsync(
        Stream source,
        FileStream target,
        long initialReceived,
        long? total,
        IProgress<DownloadProgress>? progress,
        (string MetaPath, GitHubAsset Asset)? checkpoint,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long received = initialReceived;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var lastCheckpoint = TimeSpan.Zero;
        var checkpointed = false;

        progress?.Report(new DownloadProgress(received, total, TimeSpan.Zero) { ResumedBytes = initialReceived });
        int read;
        while ((read = await ReadChunkAsync(source, buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            if (checkpoint is { } cp && (!checkpointed || stopwatch.Elapsed - lastCheckpoint >= ProgressInterval))
            {
                checkpointed = true;
                lastCheckpoint = stopwatch.Elapsed;
                await TryCheckpointAsync(target, cp.MetaPath, cp.Asset, received, cancellationToken).ConfigureAwait(false);
            }

            if (stopwatch.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                progress?.Report(new DownloadProgress(received, total, stopwatch.Elapsed) { ResumedBytes = initialReceived });
            }
        }

        if (total is { } expected && received != expected)
        {
            throw new UpdaterException($"Download truncated: expected {expected} bytes but received {received}.");
        }

        progress?.Report(new DownloadProgress(received, total ?? received, stopwatch.Elapsed) { ResumedBytes = initialReceived });
    }

    /// <summary>
    /// Reads the next chunk of <paramref name="source"/>, failing with <see cref="TimeoutException"/> when no data
    /// arrives within <see cref="IdleTimeout"/>. Each read gets its own timer, so time spent writing or
    /// checkpointing between reads never counts towards it.
    /// </summary>
    private async ValueTask<int> ReadChunkAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_idleTimeout is not { } timeout || timeout == Timeout.InfiniteTimeSpan)
        {
            return await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(timeout);
        try
        {
            return await source.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && idleCts.IsCancellationRequested)
        {
            throw new TimeoutException($"No data received from the asset stream for {timeout}.", ex);
        }
    }

    /// <summary>
    /// Fixed cross-platform set (not <see cref="Path.GetInvalidFileNameChars"/>, which varies by OS and
    /// would leave e.g. ':' and '?' unsanitized when running on Linux CI).
    /// </summary>
    private static readonly char[] InvalidFileNameChars =
        "\"<>|:*?/\\".ToCharArray()
            .Concat(Enumerable.Range(0, 32).Select(i => (char)i))
            .ToArray();

    /// <summary>
    /// Replaces invalid file-name characters with <c>_</c> and rejects names that are empty or refer to the current/parent directory.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c).ToArray()).Trim();
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            throw new ArgumentException($"Invalid asset file name '{name}'.", nameof(name));
        }
        return cleaned;
    }

    /// <summary>
    /// Deletes a file if it exists, silently ignoring I/O and permission errors.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
