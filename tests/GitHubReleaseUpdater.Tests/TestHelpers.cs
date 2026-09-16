using System.Net;
using System.Text;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Tests;

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
    }
}

internal static class TestData
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    public static GitHubReleaseClient Client(StubHttpHandler handler, string? token = null, Uri? baseUrl = null)
        => new(baseUrl, token, "tests", new HttpClient(handler));

    public static GitHubRelease Release(string tag, params string[] assetNames) => new()
    {
        Id = 1,
        TagName = tag,
        Assets = assetNames.Select((n, i) => new GitHubAsset
        {
            Id = i + 1,
            Name = n,
            Size = 10,
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

    public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) => Task.FromResult(Latest);

    public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GitHubRelease>>(All.Take(perPage).ToArray());

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
