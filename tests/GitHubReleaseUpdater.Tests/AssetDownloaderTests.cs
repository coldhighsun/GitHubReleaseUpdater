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
    public async Task Truncated_download_throws_and_cleans_up()
    {
        var client = new FakeReleaseClient();
        client.AssetBytes["a.bin"] = [1, 2, 3];
        var asset = new GitHubAsset { Name = "a.bin", Size = 10 }; // FakeReleaseClient reports actual length, so override via ReportLength=false
        client.ReportLength = false;

        await Assert.ThrowsAsync<UpdaterException>(() => new AssetDownloader(client).DownloadAsync(asset, _dir));

        Assert.Empty(Directory.GetFiles(_dir));
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
    public async Task Sanitizes_invalid_file_name_characters()
    {
        var client = new FakeReleaseClient();
        client.AssetBytes["a:b?.bin"] = [1];
        var asset = new GitHubAsset { Name = "a:b?.bin" };
        var path = await new AssetDownloader(client).DownloadAsync(asset, _dir);
        Assert.Equal("a_b_.bin", Path.GetFileName(path));
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
}
