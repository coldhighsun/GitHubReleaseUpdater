using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.LastCheck;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater.Tests;

/// <summary>
/// <see cref="ILastCheckStore"/> whose <see cref="GetLastCheckedAtAsync"/> always throws, for exercising
/// <see cref="ReleaseUpdater.CheckForUpdateAsync"/>'s exception capture.
/// </summary>
internal sealed class ThrowingLastCheckStore(Exception error) : ILastCheckStore
{
    public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default) => throw error;
    public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default) => throw error;
    public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default) => throw error;
    public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default) => throw error;
    public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default) => throw error;
}

/// <summary>
/// Routes requests to canned responses by URL substring.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    /// <summary>
    /// Registered (predicate, response factory) pairs tried in order for each request.
    /// </summary>
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public StubHttpHandler On(string urlContains, HttpStatusCode status, string body, string contentType = "application/json", Action<HttpResponseMessage>? configure = null)
    {
        _routes.Add((r => r.RequestUri!.ToString().Contains(urlContains, StringComparison.Ordinal), _ =>
        {
            var resp = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
            configure?.Invoke(resp);
            return resp;
        }));
        return this;
    }

    public StubHttpHandler OnBytes(string urlContains, byte[] body, bool includeLength = true)
    {
        _routes.Add((r => r.RequestUri!.ToString().Contains(urlContains, StringComparison.Ordinal), _ =>
        {
            HttpContent content = includeLength ? new ByteArrayContent(body) : new StreamContentNoLength(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        return this;
    }

    /// <summary>
    /// Serves <paramref name="fullBody"/> like a range-aware server: a request carrying a <c>Range: bytes=N-</c>
    /// header gets back <c>206 Partial Content</c> with <c>Content-Range: bytes N-{end}/{length}</c> and the tail
    /// of the body from byte N; a request without one gets the full body as a normal <c>200 OK</c>. When
    /// <paramref name="reportTotalLength"/> is false the total is sent as unknown (<c>bytes N-{end}/*</c>). The
    /// partial body stops at <paramref name="rangeEnd"/> (inclusive) when given, like a server that serves less
    /// than was asked for, and omits <c>Content-Length</c> when <paramref name="includeLength"/> is false.
    /// </summary>
    public StubHttpHandler OnRangeAwareBytes(string urlContains, byte[] fullBody, bool reportTotalLength = true, long? rangeEnd = null, bool includeLength = true)
    {
        _routes.Add((r => r.RequestUri!.ToString().Contains(urlContains, StringComparison.Ordinal), r =>
        {
            var rangeStart = r.Headers.Range?.Ranges.FirstOrDefault()?.From;
            if (rangeStart is { } start && start > 0)
            {
                var end = rangeEnd ?? fullBody.Length - 1;
                var tail = fullBody[(int)start..(int)(end + 1)];
                HttpContent content = includeLength ? new ByteArrayContent(tail) : new StreamContentNoLength(tail);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
                response.Content.Headers.ContentRange = reportTotalLength
                    ? new ContentRangeHeaderValue(start, end, fullBody.Length)
                    : new ContentRangeHeaderValue(start, end);
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fullBody) };
        }));
        return this;
    }

    /// <summary>
    /// Serves <paramref name="fullBody"/> like a misbehaving server: a request carrying a <c>Range</c> header gets
    /// back <c>206 Partial Content</c> whose body is the whole asset from byte 0 and whose <c>Content-Range</c>
    /// starts at <paramref name="reportedFrom"/> (omitted when null) instead of the requested offset; a request
    /// without one gets the full body as a normal <c>200 OK</c>.
    /// </summary>
    public StubHttpHandler OnMisalignedRangeBytes(string urlContains, byte[] fullBody, long? reportedFrom, long? reportedLength = null)
    {
        _routes.Add((r => r.RequestUri!.ToString().Contains(urlContains, StringComparison.Ordinal), r =>
        {
            if (r.Headers.Range is null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fullBody) };
            }
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(fullBody) };
            if (reportedFrom is { } from)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, fullBody.Length - 1, reportedLength ?? fullBody.Length);
            }
            return response;
        }));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        foreach (var (match, respond) in _routes)
        {
            if (match(request)) return Task.FromResult(respond(request));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}", Encoding.UTF8, "application/json") });
    }

    /// <summary>
    /// HTTP content that reports an unknown length, simulating a server that doesn't send <c>Content-Length</c>.
    /// </summary>
    private sealed class StreamContentNoLength(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        /// <summary>
        /// Returns the body without buffering it first, since buffering would make the base class report a length.
        /// </summary>
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(data, writable: false));
    }
}

/// <summary>
/// Never responds until the request is cancelled, for exercising <see cref="UpdaterOptions.Timeout"/>.
/// </summary>
/// <summary>
/// Handler that cancels the request on its own (as a handler-level timeout or <see cref="HttpClient.CancelPendingRequests"/>
/// would), without the caller's token or <see cref="HttpClient.Timeout"/> being involved.
/// </summary>
internal sealed class SelfCancellingHttpHandler : HttpMessageHandler
{
    /// <summary>
    /// Always throws <see cref="TaskCanceledException"/> without an inner <see cref="TimeoutException"/>.
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new TaskCanceledException("cancelled by the handler");
}

internal sealed class DelayingHttpHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

internal static class TestData
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    public static GitHubReleaseClient Client(StubHttpHandler handler, string? token = null, Uri? baseUrl = null)
        => new(baseUrl, token, "tests", new HttpClient(handler));

    public static GitHubReleaseClient Client(HttpMessageHandler handler, TimeSpan? timeout)
        => new(null, null, "tests", new HttpClient(handler), timeout);

    public static GitHubRelease Release(string tag, params string[] assetNames) => Release(tag, 10, assetNames);

    /// <summary>
    /// Builds a release whose assets all list <paramref name="assetSize"/> as their size, for tests whose asset body
    /// must match the listed size (the downloader rejects a body whose total differs from it).
    /// </summary>
    public static GitHubRelease Release(string tag, long assetSize, params string[] assetNames) => new()
    {
        Id = 1,
        TagName = tag,
        Assets = assetNames.Select((n, i) => new GitHubAsset
        {
            Id = i + 1,
            Name = n,
            Size = assetSize,
            BrowserDownloadUrl = $"https://github.com/o/r/releases/download/{tag}/{n}",
            ApiUrl = $"https://api.github.com/repos/o/r/releases/assets/{i + 1}",
        }).ToArray(),
    };
}

/// <summary>
/// In-memory <see cref="IGitHubReleaseClient"/> for facade tests.
/// </summary>
internal sealed class FakeReleaseClient : IGitHubReleaseClient
{
    public GitHubRelease? Latest { get; set; }
    public List<GitHubRelease> All { get; } = [];
    public Dictionary<string, byte[]> AssetBytes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ReportLength { get; set; } = true;

    /// <summary>When set, <see cref="GetLatestReleaseAsync"/> and <see cref="ListReleasesAsync"/> throw this instead of returning.</summary>
    public Exception? ThrowOnFetch { get; set; }

    public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default)
        => ThrowOnFetch is not null ? throw ThrowOnFetch : Task.FromResult(Latest);

    public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default)
        => ThrowOnFetch is not null ? throw ThrowOnFetch : Task.FromResult<IReadOnlyList<GitHubRelease>>(All.Take(perPage).ToArray());

    public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default)
        => Task.FromResult(All.FirstOrDefault(r => r.TagName == tag));

    public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
    {
        var bytes = AssetBytes[asset.Name];
        return Task.FromResult(new AssetStream(new MemoryStream(bytes), ReportLength ? bytes.Length : null, new MemoryStream()));
    }

    public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
        => Task.FromResult(Encoding.UTF8.GetString(AssetBytes[asset.Name]));
}

/// <summary>
/// <see cref="IGitHubReleaseClient"/> whose <see cref="OpenAssetStreamAsync"/> throws <see cref="FailUntilAttempt"/>
/// times before finally returning <see cref="Bytes"/>, for exercising <see cref="Download.AssetDownloader"/>'s retry loop.
/// </summary>
internal sealed class FlakyAssetClient : IGitHubReleaseClient
{
    /// <summary>Bytes returned once the attempt count reaches <see cref="FailUntilAttempt"/>.</summary>
    public byte[] Bytes { get; set; } = [];

    /// <summary>Number of leading attempts that throw <see cref="ExceptionFactory"/> before one succeeds.</summary>
    public int FailUntilAttempt { get; set; }

    /// <summary>Exception thrown by each failing attempt. Defaults to an <see cref="HttpRequestException"/>.</summary>
    public Func<Exception> ExceptionFactory { get; set; } = () => new HttpRequestException("simulated transient failure");

    /// <summary>Number of calls made to <see cref="OpenAssetStreamAsync"/> so far.</summary>
    public int Attempts { get; private set; }

    public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
    {
        Attempts++;
        if (Attempts <= FailUntilAttempt)
            throw ExceptionFactory();
        return Task.FromResult(new AssetStream(new MemoryStream(Bytes), Bytes.Length, new MemoryStream()));
    }

    public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// <see cref="IGitHubReleaseClient"/> that honors range requests like a real server, for exercising
/// <see cref="Download.AssetDownloader"/>'s resume logic end to end.
/// </summary>
internal sealed class ResumableAssetClient : IGitHubReleaseClient
{
    /// <summary>The complete asset content.</summary>
    public byte[] FullBytes { get; set; } = [];

    /// <summary>When true (the default), a non-zero <c>rangeStart</c> yields a partial stream starting there; when false, every call returns the full body from byte 0.</summary>
    public bool HonorRange { get; set; } = true;

    /// <summary>1-based attempt number that throws <see cref="HttpRequestException"/> after <see cref="FailAfterBytes"/> bytes; 0 disables failure injection.</summary>
    public int FailAfterBytesOnAttempt { get; set; }

    /// <summary>Bytes yielded on the failing attempt before it throws.</summary>
    public int FailAfterBytes { get; set; }

    /// <summary>
    /// Invoked on the failing attempt right before the simulated failure is thrown, e.g. to cancel the caller's token
    /// at a deterministic point.
    /// </summary>
    public Action? BeforeFailure { get; set; }

    /// <summary>Number of calls made to <see cref="OpenAssetStreamAsync(GitHubAsset, long, CancellationToken)"/> so far.</summary>
    public int Attempts { get; private set; }

    /// <summary><c>rangeStart</c> passed on each call, in order.</summary>
    public List<long> RequestedRangeStarts { get; } = [];

    public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
        => OpenAssetStreamAsync(asset, 0, cancellationToken);

    public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, long rangeStart, CancellationToken cancellationToken = default)
    {
        Attempts++;
        RequestedRangeStarts.Add(rangeStart);
        var effectiveStart = HonorRange ? rangeStart : 0;
        var remaining = FullBytes[(int)effectiveStart..];
        Stream stream = Attempts == FailAfterBytesOnAttempt ? new FailingAfterStream(remaining, FailAfterBytes, BeforeFailure) : new MemoryStream(remaining);
        var isPartial = HonorRange && rangeStart > 0;
        return Task.FromResult(new AssetStream(stream, remaining.Length, new MemoryStream(), isPartial, rangeStart, FullBytes.Length));
    }

    public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>
    /// Stream that yields <paramref name="data"/> up to <paramref name="failAfter"/> bytes, then throws
    /// <see cref="HttpRequestException"/> as if the connection broke mid-transfer.
    /// </summary>
    private sealed class FailingAfterStream(byte[] data, int failAfter, Action? beforeFailure) : Stream
    {
        private int _position;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= failAfter || _position >= data.Length)
            {
                beforeFailure?.Invoke();
                throw new HttpRequestException("simulated connection reset mid-transfer");
            }
            var toCopy = Math.Min(buffer.Length, Math.Min(data.Length - _position, failAfter - _position));
            data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
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
