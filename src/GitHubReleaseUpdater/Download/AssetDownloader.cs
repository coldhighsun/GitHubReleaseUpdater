using System.Diagnostics;
using System.Net;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;

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
    /// belongs to, so a stale partial from a different asset/release is never resumed into.
    /// </summary>
    private const string MetaSuffix = ".meta";

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
    /// A transient failure (network I/O error, request timeout, or a truncated body) is retried with exponential
    /// backoff starting at <see cref="RetryDelay"/> and doubling each attempt; a checksum mismatch, disk error,
    /// invalid argument, or caller cancellation is never retried. Default 2 (3 attempts total).
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
    /// the process was killed) — is resumed via an HTTP range request instead of re-downloaded from byte 0. If the
    /// underlying <see cref="IGitHubReleaseClient"/> doesn't support range requests (its
    /// <see cref="IGitHubReleaseClient.OpenAssetStreamAsync(GitHub.Models.GitHubAsset, long, CancellationToken)"/>
    /// returns a non-partial stream) the download transparently restarts from 0 instead. When false, every attempt
    /// starts from 0 and any failure (other than a successful completion) deletes the partial file, matching this
    /// type's behavior before resume support was added.
    /// </summary>
    public bool AllowResume { get; init; } = true;

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
    /// <param name="overwrite">Whether to replace an existing file at the destination.</param>
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
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var name = SanitizeFileName(fileName ?? asset.Name);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, name);
        var partialPath = finalPath + PartialSuffix;
        var metaPath = partialPath + MetaSuffix;

        if (File.Exists(finalPath) && !overwrite)
            throw new IOException($"File already exists: {finalPath}");

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Only trust a pre-existing partial file when overwrite allows touching what's on disk and its
                // sidecar .meta confirms it belongs to this exact asset (id + size) — otherwise start from 0.
                var resumeFrom = overwrite && AllowResume && PartialMatchesAsset(partialPath, metaPath, asset)
                    ? new FileInfo(partialPath).Length
                    : 0;
                await using var source = await _client.OpenAssetStreamAsync(asset, resumeFrom, cancellationToken).ConfigureAwait(false);
                var resuming = resumeFrom > 0 && source.IsPartial;
                var fileMode = resuming ? FileMode.Append : FileMode.Create;
                var initialReceived = resuming ? resumeFrom : 0L;
                var total = source.TotalLength ?? (asset.Size > 0 ? asset.Size : null);

                await using (var target = new FileStream(partialPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    // Only now that FileMode.Create has actually truncated the file (done synchronously by the
                    // FileStream constructor above) can the marker truthfully claim this asset's identity for it.
                    if (!resuming) WriteAssetMarker(metaPath, asset);

                    await CopyWithProgressAsync(source.Stream, target, initialReceived, total, progress, cancellationToken).ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(partialPath, finalPath);
                TryDelete(metaPath);
                return finalPath;
            }
            // A range that's no longer valid for this asset (e.g. it was regenerated with a smaller size between
            // attempts) can never succeed by resuming again, so the partial is discarded unconditionally — even
            // when AllowResume is true — so this and any later call restarts from 0 instead of failing forever.
            catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                TryDelete(partialPath);
                TryDelete(metaPath);
                if (attempt >= MaxRetryAttempts) throw;
                await Task.Delay(RetryDelay * Math.Pow(2, attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxRetryAttempts && IsTransient(ex))
            {
                if (!AllowResume)
                {
                    TryDelete(partialPath);
                    TryDelete(metaPath);
                }
                await Task.Delay(RetryDelay * Math.Pow(2, attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
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

    /// <summary>
    /// True when both <paramref name="partialPath"/> and its sidecar <paramref name="metaPath"/> exist and the
    /// marker records the same asset id and size as <paramref name="asset"/>. False (and any stale sidecar/partial
    /// deleted) for a missing partial, a missing/unreadable/mismatched marker, or an orphaned marker whose partial
    /// file is gone.
    /// </summary>
    private static bool PartialMatchesAsset(string partialPath, string metaPath, GitHubAsset asset)
    {
        if (!File.Exists(partialPath))
        {
            TryDelete(metaPath);
            return false;
        }

        string? stored;
        try
        {
            if (!File.Exists(metaPath)) return false;
            stored = File.ReadAllText(metaPath);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        if (stored == AssetMarker(asset)) return true;

        TryDelete(metaPath);
        return false;
    }

    /// <summary>
    /// Writes the sidecar <c>.meta</c> file recording which asset a freshly (re)started partial download belongs to.
    /// </summary>
    private static void WriteAssetMarker(string metaPath, GitHubAsset asset)
    {
        try
        {
            File.WriteAllText(metaPath, AssetMarker(asset));
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
    /// True for exceptions worth retrying: transport-level failures (<see cref="HttpRequestException"/> and
    /// <see cref="System.Net.Http.HttpIOException"/>, the latter thrown when the response body stream breaks
    /// mid-transfer, e.g. a reset connection), request timeouts, and a truncated body (an exact
    /// <see cref="UpdaterException"/>, not one of its subclasses such as <see cref="ChecksumMismatchException"/>).
    /// Deliberately excludes plain <see cref="IOException"/> so local disk errors (full disk, locked file) writing
    /// the partial file fail fast instead of retrying a doomed operation. False for caller cancellation, argument
    /// errors, and every other library exception.
    /// </summary>
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TimeoutException or System.Net.Http.HttpIOException || ex.GetType() == typeof(UpdaterException);

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> in <see cref="BufferSize"/> chunks, reporting
    /// progress at most every <see cref="ProgressInterval"/> and throwing if the final received byte count (starting
    /// from <paramref name="initialReceived"/>, non-zero when resuming a partial download) falls short of <paramref name="total"/>.
    /// </summary>
    private async Task CopyWithProgressAsync(Stream source, Stream target, long initialReceived, long? total, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long received = initialReceived;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        progress?.Report(new DownloadProgress(received, total, TimeSpan.Zero));
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (progress is not null && stopwatch.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                progress.Report(new DownloadProgress(received, total, stopwatch.Elapsed));
            }
        }

        if (total is { } expected && received != expected)
            throw new UpdaterException($"Download truncated: expected {expected} bytes but received {received}.");

        progress?.Report(new DownloadProgress(received, total ?? received, stopwatch.Elapsed));
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
            throw new ArgumentException($"Invalid asset file name '{name}'.", nameof(name));
        return cleaned;
    }

    /// <summary>
    /// Deletes a file if it exists, silently ignoring I/O and permission errors.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
