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
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "a.bin.partial"), [9, 9]); // leftover bytes from a run against a server that ignores ranges
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.bin.partial.meta"), $"{asset.ApiUrl}:{asset.Id}:{asset.Size}"); // marks the leftover bytes as belonging to this exact asset

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
    public async Task Does_not_retry_github_api_exception_even_though_it_derives_from_updater_exception()
    {
        var client = new FlakyAssetClient
        {
            Bytes = [1],
            FailUntilAttempt = 1,
            ExceptionFactory = () => new GitHubApiException("rate limited"),
        };
        var asset = new GitHubAsset { Name = "a.bin" };
        var downloader = new AssetDownloader(client) { RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<GitHubApiException>(() => downloader.DownloadAsync(asset, _dir));

        Assert.Equal(1, client.Attempts);
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
