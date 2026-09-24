using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Tests;

public class AssetDownloaderTests : IDisposable
{
    /// <summary>
    /// Temporary directory used as the download destination, removed after each test.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gru-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Downloads_to_final_path_and_reports_progress()
    {
        var data = new byte[300_000];
        Random.Shared.NextBytes(data);
        var client = new FakeReleaseClient();
        client.AssetBytes["a.bin"] = data;
        var asset = TestData.Release("v1", "a.bin").Assets[0];
        var reports = new List<DownloadProgress>();
        var downloader = new AssetDownloader(client) { BufferSize = 4096, ProgressInterval = TimeSpan.Zero };

        var path = await downloader.DownloadAsync(asset, _dir, progress: new SyncProgress(reports));

        Assert.Equal(Path.Combine(_dir, "a.bin"), path);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".partial"));
        Assert.True(reports.Count > 2);
        Assert.Equal(0, reports[0].BytesReceived);
        Assert.Equal(data.Length, reports[^1].BytesReceived);
        Assert.Equal(100, reports[^1].Percentage);
    }

    [Fact]
    public async Task Unknown_length_still_completes()
    {
        var client = new FakeReleaseClient { ReportLength = false };
        client.AssetBytes["a.bin"] = [1, 2, 3];
        var asset = new GitHubAsset { Name = "a.bin", Size = 0 };
        var reports = new List<DownloadProgress>();

        await new AssetDownloader(client).DownloadAsync(asset, _dir, progress: new SyncProgress(reports));

        Assert.Null(reports[0].TotalBytes);
        Assert.Equal(3, reports[^1].TotalBytes);
    }

    [Fact]
    public async Task Truncated_download_throws_and_cleans_up_when_resume_is_disabled()
    {
        var client = new FakeReleaseClient();
        client.AssetBytes["a.bin"] = [1, 2, 3];
        var asset = new GitHubAsset { Name = "a.bin", Size = 10 }; // FakeReleaseClient reports actual length, so override via ReportLength=false
        client.ReportLength = false;
        var downloader = new AssetDownloader(client) { AllowResume = false, RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<UpdaterException>(() => downloader.DownloadAsync(asset, _dir));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Exhausted_failure_leaves_partial_file_for_a_later_resume_by_default()
    {
        var client = new FakeReleaseClient();
        client.AssetBytes["a.bin"] = [1, 2, 3];
        var asset = new GitHubAsset { Name = "a.bin", Size = 10 };
        client.ReportLength = false;
        var downloader = new AssetDownloader(client) { MaxRetryAttempts = 1, RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<UpdaterException>(() => downloader.DownloadAsync(asset, _dir));

        var partialPath = Path.Combine(_dir, "a.bin.partial");
        Assert.True(File.Exists(partialPath));
    }

    [Fact]
    public async Task Cancellation_removes_partial_file()
    {
        var client = new BlockingClient();
        var asset = new GitHubAsset { Name = "a.bin" };
        using var cts = new CancellationTokenSource();
        var task = new AssetDownloader(client).DownloadAsync(asset, _dir, cancellationToken: cts.Token);
        await client.Started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Resumes_from_partial_file_after_transient_failure_within_retry()
    {
        var full = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var client = new ResumableAssetClient { FullBytes = full, FailAfterBytesOnAttempt = 1, FailAfterBytes = 4 };
        var asset = new GitHubAsset { Name = "a.bin", Size = full.Length };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        var path = await downloader.DownloadAsync(asset, _dir);

        Assert.Equal(full, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, client.Attempts);
        Assert.Equal([0, 4], client.RequestedRangeStarts);
    }

    [Fact]
    public async Task Resumes_across_separate_download_calls_after_process_restart()
    {
        var full = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var asset = new GitHubAsset { Name = "a.bin", Size = full.Length };

        var crashingClient = new ResumableAssetClient { FullBytes = full, FailAfterBytesOnAttempt = 1, FailAfterBytes = 4 };
        // No retries: simulates the process being killed before AssetDownloader itself could retry.
        var crashingDownloader = new AssetDownloader(crashingClient) { MaxRetryAttempts = 0 };
        await Assert.ThrowsAsync<HttpRequestException>(() => crashingDownloader.DownloadAsync(asset, _dir));
        Assert.Equal(4, new FileInfo(Path.Combine(_dir, "a.bin.partial")).Length);

        // A brand-new AssetDownloader over a working client (simulating the app restarting) picks up where it left off.
        var resumedClient = new ResumableAssetClient { FullBytes = full };
        var path = await new AssetDownloader(resumedClient).DownloadAsync(asset, _dir);

        Assert.Equal(full, await File.ReadAllBytesAsync(path));
        Assert.Equal([4], resumedClient.RequestedRangeStarts);
    }

    [Fact]
    public async Task Resumes_even_when_overwrite_is_disabled()
    {
        var full = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var asset = new GitHubAsset { Name = "a.bin", Size = full.Length };

        var crashingClient = new ResumableAssetClient { FullBytes = full, FailAfterBytesOnAttempt = 1, FailAfterBytes = 4 };
        var crashingDownloader = new AssetDownloader(crashingClient) { MaxRetryAttempts = 0 };
        await Assert.ThrowsAsync<HttpRequestException>(() => crashingDownloader.DownloadAsync(asset, _dir));

        // overwrite: false protects a completed final file from being replaced; it must not also disable resuming
        // the in-progress partial, since no completed file exists yet at this point.
        var resumedClient = new ResumableAssetClient { FullBytes = full };
        var path = await new AssetDownloader(resumedClient).DownloadAsync(asset, _dir, overwrite: false);

        Assert.Equal(full, await File.ReadAllBytesAsync(path));
        Assert.Equal([4], resumedClient.RequestedRangeStarts);
    }

    [Fact]
    public async Task Allow_resume_false_ignores_existing_partial_file_and_restarts_from_zero()
    {
        var full = new byte[] { 1, 2, 3, 4, 5 };
        var asset = new GitHubAsset { Name = "a.bin", Size = full.Length };
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "a.bin.partial"), [9, 9]); // stale bytes left over from an earlier run

        var client = new ResumableAssetClient { FullBytes = full };
        var path = await new AssetDownloader(client) { AllowResume = false }.DownloadAsync(asset, _dir);

        Assert.Equal(full, await File.ReadAllBytesAsync(path));
        Assert.Equal([0L], client.RequestedRangeStarts);
    }

    [Fact]
    public async Task Falls_back_to_full_restart_when_server_does_not_honor_range()
    {
        var full = new byte[] { 1, 2, 3, 4, 5 };
        var asset = new GitHubAsset { Id = 1, Name = "a.bin", Size = full.Length, ApiUrl = "https://api.example/assets/1" };
        var stale = new byte[] { 9, 9 }; // leftover bytes from a run against a server that ignores ranges
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "a.bin.partial"), stale);
        // Marks the leftover bytes as belonging to this exact asset and verified up to their full length.
        var tailHash = Convert.ToHexStringLower(MD5.HashData(stale));
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.bin.partial.meta"), $"{asset.ApiUrl}:{asset.Id}:{asset.Size}\n{stale.Length}:{tailHash}");

        var client = new ResumableAssetClient { FullBytes = full, HonorRange = false };
        var path = await new AssetDownloader(client).DownloadAsync(asset, _dir);

        Assert.Equal(full, await File.ReadAllBytesAsync(path));
        Assert.Equal([2L], client.RequestedRangeStarts); // asked to resume at 2, but the server ignored it
    }

    [Fact]
    public async Task Refuses_overwrite_when_disabled()
    {
        var client = new FakeReleaseClient();
        client.AssetBytes["a.bin"] = [1];
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client);
        await downloader.DownloadAsync(asset, _dir);

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(asset, _dir, overwrite: false));
        await downloader.DownloadAsync(asset, _dir, overwrite: true);
    }

    [Fact]
    public async Task Retries_transient_failure_and_succeeds()
    {
        var client = new FlakyAssetClient { Bytes = [1, 2, 3], FailUntilAttempt = 2 };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        var path = await downloader.DownloadAsync(asset, _dir);

        Assert.Equal(3, client.Attempts);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Gives_up_after_exhausting_retry_attempts()
    {
        var client = new FlakyAssetClient { Bytes = [1], FailUntilAttempt = int.MaxValue };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { MaxRetryAttempts = 2, RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(asset, _dir));

        Assert.Equal(3, client.Attempts); // 1 initial attempt + 2 retries
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Does_not_retry_non_transient_failure()
    {
        var client = new FlakyAssetClient { Bytes = [1], FailUntilAttempt = 1, ExceptionFactory = () => new ArgumentException("not transient") };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(asset, _dir));

        Assert.Equal(1, client.Attempts);
    }

    [Fact]
    public async Task Does_not_retry_local_disk_io_error()
    {
        var client = new FlakyAssetClient { Bytes = [1], FailUntilAttempt = 1, ExceptionFactory = () => new IOException("disk full") };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(asset, _dir));

        Assert.Equal(1, client.Attempts);
    }

    [Fact]
    public async Task Retries_http_io_exception()
    {
        var client = new FlakyAssetClient
        {
            Bytes = [1, 2, 3],
            FailUntilAttempt = 1,
            ExceptionFactory = () => new System.Net.Http.HttpIOException(System.Net.Http.HttpRequestError.ResponseEnded, "connection reset mid-transfer"),
        };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        var path = await downloader.DownloadAsync(asset, _dir);

        Assert.Equal(2, client.Attempts);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Retries_timeout_exception()
    {
        var client = new FlakyAssetClient { Bytes = [1], FailUntilAttempt = 1, ExceptionFactory = () => new TimeoutException("request timed out") };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        var path = await downloader.DownloadAsync(asset, _dir);

        Assert.Equal(2, client.Attempts);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Retries_truncated_download()
    {
        // First attempt reports a length longer than the bytes actually returned, so CopyWithProgressAsync
        // throws the exact UpdaterException that AssetDownloader treats as a transient truncation.
        var client = new TruncatingThenCompleteClient();
        var asset = new GitHubAsset { Name = "a.bin", Size = 3 };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        var path = await downloader.DownloadAsync(asset, _dir);

        Assert.Equal(2, client.Attempts);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Does_not_retry_non_transient_github_api_exception()
    {
        var client = new FlakyAssetClient
        {
            Bytes = [1],
            FailUntilAttempt = 1,
            ExceptionFactory = () => new GitHubApiException("not found", HttpStatusCode.NotFound, false, null, string.Empty),
        };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<GitHubApiException>(() => downloader.DownloadAsync(asset, _dir));

        Assert.Equal(1, client.Attempts);
    }

    [Fact]
    public async Task Retries_transient_github_api_exception()
    {
        var client = new FlakyAssetClient
        {
            Bytes = [1, 2, 3],
            FailUntilAttempt = 1,
            ExceptionFactory = () => new GitHubApiException("service unavailable", HttpStatusCode.ServiceUnavailable, false, null, string.Empty),
        };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        var path = await downloader.DownloadAsync(asset, _dir);

        Assert.Equal(2, client.Attempts);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Zero_max_retry_attempts_disables_retrying()
    {
        var client = new FlakyAssetClient { Bytes = [1], FailUntilAttempt = 1 };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { MaxRetryAttempts = 0, RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(asset, _dir));

        Assert.Equal(1, client.Attempts);
    }

    [Fact]
    public async Task Does_not_retry_caller_cancellation()
    {
        var client = new BlockingClient();
        var asset = new GitHubAsset { Name = "a.bin" };
        using var cts = new CancellationTokenSource();
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };
        var task = downloader.DownloadAsync(asset, _dir, cancellationToken: cts.Token);
        await client.Started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    /// <summary>
    /// A caller cancellation that lands during the backoff between retries (rather than mid-transfer) must still
    /// remove the partial file left by the failed attempt, even though resume is enabled.
    /// </summary>
    [Fact]
    public async Task DownloadAsync_cancelled_during_retry_backoff_removes_partial_file()
    {
        using var cts = new CancellationTokenSource();
        // Cancel exactly when the transient failure is raised, so the cancellation is first observed by the backoff
        // wait rather than depending on a timer racing the download.
        var client = new ResumableAssetClient { FullBytes = [1, 2, 3, 4, 5], FailAfterBytesOnAttempt = 1, FailAfterBytes = 2, BeforeFailure = cts.Cancel };
        var asset = new GitHubAsset { Name = "a.bin", Size = 5, ApiUrl = "https://api.example/assets/1" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.FromMinutes(1) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(asset, _dir, cancellationToken: cts.Token));

        Assert.Equal(1, client.Attempts);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>
    /// The backoff must stay within what <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts, however many
    /// attempts have been made or however large the base delay is.
    /// </summary>
    [Theory]
    [InlineData(1_000L, 40)]
    [InlineData(long.MaxValue / TimeSpan.TicksPerMillisecond, 0)]
    public void ExponentialDelay_result_exceeding_task_delay_limit_is_clamped(long baseDelayMs, int attempt)
    {
        var delay = AssetDownloader.ExponentialDelay(TimeSpan.FromMilliseconds(baseDelayMs), attempt);

        Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 1), delay);
        _ = Task.Delay(delay, new CancellationToken(canceled: true)); // Throws ArgumentOutOfRangeException if out of range.
    }

    /// <summary>
    /// A zero base delay must stay zero even when <c>2^attempt</c> overflows to infinity (where <c>0 * infinity</c>
    /// would otherwise be NaN).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1_024)]
    [InlineData(int.MaxValue)]
    public void ExponentialDelay_zero_base_delay_returns_zero(int attempt)
    {
        var delay = AssetDownloader.ExponentialDelay(TimeSpan.Zero, attempt);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_per_path_lock_does_not_leak_the_lock_entry()
    {
        var asset = new GitHubAsset { Name = "a.bin" };
        var holder = new BlockingClient();
        using var holderCts = new CancellationTokenSource();
        var holderTask = new AssetDownloader(holder).DownloadAsync(asset, _dir, cancellationToken: holderCts.Token);
        await holder.Started.Task; // Holder now owns the per-path lock and is blocked reading.

        var pathLocksField = typeof(AssetDownloader).GetField("PathLocks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var pathLocks = (System.Collections.IDictionary)pathLocksField.GetValue(null)!;
        var pathKey = Path.GetFullPath(Path.Combine(_dir, "a.bin.partial"));
        var refCountField = pathLocks[pathKey]!.GetType().GetField("RefCount")!;

        // A second caller for the same destination is cancelled before it ever acquires the lock the holder owns.
        using var waiterCts = new CancellationTokenSource();
        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new AssetDownloader(new BlockingClient()).DownloadAsync(asset, _dir, cancellationToken: waiterCts.Token));

        // The cancelled waiter must not leave its increment behind on the holder's still-live lock entry.
        Assert.Equal(1, refCountField.GetValue(pathLocks[pathKey]));

        holderCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holderTask);

        // Once the sole remaining holder releases it, the entry must be evicted rather than left behind forever.
        Assert.False(pathLocks.Contains(pathKey));
    }

    [Fact]
    public async Task Malformed_checkpoint_line_deletes_the_stale_meta_file()
    {
        var asset = new GitHubAsset { Id = 1, Name = "a.bin", Size = 5, ApiUrl = "https://api.example/assets/1" };
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "a.bin.partial"), [1, 2, 3]);
        // Identity line matches, but the checkpoint line isn't the expected "<length>:<hash>" format.
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.bin.partial.meta"), $"{asset.ApiUrl}:{asset.Id}:{asset.Size}\nnot-a-checkpoint");

        var client = new FlakyAssetClient { FailUntilAttempt = int.MaxValue, ExceptionFactory = () => new ArgumentException("boom") };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(asset, _dir));

        Assert.False(File.Exists(Path.Combine(_dir, "a.bin.partial.meta")));
        Assert.True(File.Exists(Path.Combine(_dir, "a.bin.partial")));
    }

    [Fact]
    public void ComputeTailHash_from_stream_restores_position_even_when_the_read_throws()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "x.bin");
        File.WriteAllBytes(path, [1, 2, 3]); // Only 3 bytes actually on disk.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);

        var method = typeof(AssetDownloader).GetMethod(
            "ComputeTailHash", BindingFlags.NonPublic | BindingFlags.Static, [typeof(FileStream), typeof(long)])!;

        // Ask for 10 verified bytes when only 3 exist on disk, so the tail read throws EndOfStreamException.
        Assert.ThrowsAny<Exception>(() => method.Invoke(null, [stream, 10L]));

        // A caller that swallows that exception (as the async checkpoint path does) must still see the stream
        // positioned at the requested length, not stuck mid-window, or its next write would silently overwrite
        // already-written bytes instead of appending.
        Assert.Equal(10, stream.Position);
    }

    [Fact]
    public async Task Sanitizes_invalid_file_name_characters()
    {
        var client = new FakeReleaseClient();
        client.AssetBytes["a:b?.bin"] = [1];
        var asset = new GitHubAsset { Name = "a:b?.bin" };
        var path = await new AssetDownloader(client).DownloadAsync(asset, _dir);
        Assert.Equal("a_b_.bin", Path.GetFileName(path));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("   ")]
    public async Task Rejects_asset_names_that_sanitize_to_current_or_parent_directory_or_empty(string name)
    {
        var client = new FakeReleaseClient();
        client.AssetBytes[name] = [1];
        var asset = new GitHubAsset { Name = name };

        await Assert.ThrowsAsync<ArgumentException>(() => new AssetDownloader(client).DownloadAsync(asset, _dir));
    }

    /// <summary>
    /// Collects reported progress synchronously into a list for assertions.
    /// </summary>
    private sealed class SyncProgress(List<DownloadProgress> sink) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => sink.Add(value);
    }

    /// <summary>
    /// Client whose asset stream never yields data until cancelled.
    /// </summary>
    private sealed class BlockingClient : IGitHubReleaseClient
    {
        public TaskCompletionSource Started { get; } = new();

        public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
            => Task.FromResult(new AssetStream(new BlockingStream(Started), null, new MemoryStream()));

        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>
        /// Stream whose read never completes until the wrapping token is cancelled, signaling <paramref name="started"/> first.
        /// </summary>
        private sealed class BlockingStream(TaskCompletionSource started) : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Client whose first <see cref="OpenAssetStreamAsync"/> call reports a <see cref="AssetStream.ContentLength"/>
    /// longer than the bytes it actually streams (simulating a connection that drops mid-transfer), then succeeds
    /// with the full body on the next call.
    /// </summary>
    private sealed class TruncatingThenCompleteClient : IGitHubReleaseClient
    {
        private static readonly byte[] FullBody = [1, 2, 3];

        public int Attempts { get; private set; }

        public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
        {
            Attempts++;
            var bytes = Attempts == 1 ? FullBody[..1] : FullBody;
            return Task.FromResult(new AssetStream(new MemoryStream(bytes), FullBody.Length, new MemoryStream()));
        }

        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
