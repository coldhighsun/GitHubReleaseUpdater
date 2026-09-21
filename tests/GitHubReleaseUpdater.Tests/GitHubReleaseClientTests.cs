using GitHubReleaseUpdater.Exceptions;
using System.Net;

namespace GitHubReleaseUpdater.Tests;

public class GitHubReleaseClientTests
{
    [Fact]
    public async Task GetLatest_parses_release_and_assets()
    {
        var handler = new StubHttpHandler().On("/repos/o/r/releases/latest", HttpStatusCode.OK, TestData.Read("release-latest.json"));
        using var client = TestData.Client(handler);

        var release = await client.GetLatestReleaseAsync("o", "r");

        Assert.NotNull(release);
        Assert.Equal("v2.1.0", release.TagName);
        Assert.False(release.Prerelease);
        Assert.Equal(3, release.Assets.Count);
        var zip = release.Assets[0];
        Assert.Equal("myapp-2.1.0-win-x64.zip", zip.Name);
        Assert.Equal(1234, zip.Size);
        Assert.StartsWith("sha256:", zip.Digest);
        Assert.Equal("https://api.github.com/repos/o/r/releases/assets/1", zip.ApiUrl);
    }

    [Fact]
    public async Task GetLatest_returns_null_on_404()
    {
        var handler = new StubHttpHandler();
        using var client = TestData.Client(handler);
        Assert.Null(await client.GetLatestReleaseAsync("o", "r"));
        Assert.Null(await client.GetReleaseByTagAsync("o", "r", "v0"));
    }

    [Fact]
    public async Task ListReleases_passes_paging_and_parses_array()
    {
        var handler = new StubHttpHandler().On("/releases?per_page=5&page=2", HttpStatusCode.OK, TestData.Read("releases-list.json"));
        using var client = TestData.Client(handler);

        var list = await client.ListReleasesAsync("o", "r", perPage: 5, page: 2);

        Assert.Equal(5, list.Count);
        Assert.True(list[2].Draft);
    }

    [Fact]
    public async Task Malformed_json_throws_api_exception()
    {
        var handler = new StubHttpHandler().On("/releases/latest", HttpStatusCode.OK, "{not json");
        using var client = TestData.Client(handler);
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetLatestReleaseAsync("o", "r"));
        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task OpenAssetStream_uses_api_url_with_octet_stream_accept()
    {
        var handler = new StubHttpHandler().OnBytes("/releases/assets/1", [1, 2, 3]);
        using var client = TestData.Client(handler);
        var asset = TestData.Release("v1", "a.zip").Assets[0];

        await using var stream = await client.OpenAssetStreamAsync(asset);

        Assert.Equal(3, stream.ContentLength);
        var buf = new byte[3];
        Assert.Equal(3, await stream.Stream.ReadAtLeastAsync(buf, 3));
        Assert.Contains(handler.Requests[0].Headers.Accept, a => a.MediaType == "application/octet-stream");
    }

    [Fact]
    public async Task Rate_limit_is_detected_with_reset_time()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var handler = new StubHttpHandler().On("/releases", HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}", configure: r =>
        {
            r.Headers.Add("X-RateLimit-Remaining", "0");
            r.Headers.Add("X-RateLimit-Reset", reset.ToString());
        });
        using var client = TestData.Client(handler);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.ListReleasesAsync("o", "r"));
        Assert.True(ex.IsRateLimited);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), ex.RateLimitResetAt);
        Assert.Contains("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAssetText_returns_body()
    {
        var handler = new StubHttpHandler().On("/releases/assets/1", HttpStatusCode.OK, "abc  file", "text/plain");
        using var client = TestData.Client(handler);
        var asset = TestData.Release("v1", "SHA256SUMS").Assets[0];
        Assert.Equal("abc  file", await client.ReadAssetTextAsync(asset));
    }

    [Fact]
    public async Task Sends_required_headers_and_bearer_token()
    {
        var handler = new StubHttpHandler().On("/releases/latest", HttpStatusCode.OK, TestData.Read("release-latest.json"));
        using var client = TestData.Client(handler, token: "ghp_abc");

        await client.GetLatestReleaseAsync("o", "r");

        var req = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("ghp_abc", req.Headers.Authorization.Parameter);
        Assert.Contains(req.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
        Assert.NotEmpty(req.Headers.UserAgent);
        Assert.True(req.Headers.Contains("X-GitHub-Api-Version"));
    }

    [Fact]
    public async Task Unauthorized_throws_with_status()
    {
        var handler = new StubHttpHandler().On("/releases/latest", HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}");
        using var client = TestData.Client(handler);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetLatestReleaseAsync("o", "r"));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.False(ex.IsRateLimited);
    }

    [Fact]
    public async Task Uses_custom_base_url_for_enterprise()
    {
        var handler = new StubHttpHandler().On("ghe.example.com/api/v3/repos/o/r/releases/latest", HttpStatusCode.OK, TestData.Read("release-latest.json"));
        using var client = TestData.Client(handler, baseUrl: new Uri("https://ghe.example.com/api/v3"));

        var release = await client.GetLatestReleaseAsync("o", "r");

        Assert.NotNull(release);
        Assert.Equal("https://ghe.example.com/api/v3/repos/o/r/releases/latest", handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("", "r")]
    [InlineData(" ", "r")]
    [InlineData("o", "")]
    [InlineData("o", " ")]
    public async Task GetLatest_rejects_blank_owner_or_repo(string owner, string repo)
    {
        using var client = TestData.Client(new StubHttpHandler());
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetLatestReleaseAsync(owner, repo));
    }

    [Theory]
    [InlineData(null, "r")]
    [InlineData("o", null)]
    public async Task GetLatest_rejects_null_owner_or_repo(string? owner, string? repo)
    {
        using var client = TestData.Client(new StubHttpHandler());
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetLatestReleaseAsync(owner!, repo!));
    }

    [Fact]
    public async Task GetReleaseByTag_rejects_blank_tag()
    {
        using var client = TestData.Client(new StubHttpHandler());
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetReleaseByTagAsync("o", "r", " "));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(101, 1)]
    [InlineData(1, 0)]
    public async Task ListReleases_rejects_out_of_range_paging(int perPage, int page)
    {
        using var client = TestData.Client(new StubHttpHandler());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListReleasesAsync("o", "r", perPage, page));
    }

    [Fact]
    public void Constructor_rejects_relative_base_url()
        => Assert.Throws<ArgumentException>(() => new GitHubReleaseUpdater.GitHub.GitHubReleaseClient(new Uri("relative", UriKind.Relative)));

    [Fact]
    public async Task OpenAssetStream_rejects_null_asset()
    {
        using var client = TestData.Client(new StubHttpHandler());
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenAssetStreamAsync(null!));
    }

    [Fact]
    public async Task OpenAssetStream_rejects_asset_without_download_url()
    {
        using var client = TestData.Client(new StubHttpHandler());
        var asset = new GitHubReleaseUpdater.GitHub.Models.GitHubAsset { Name = "a.zip" };
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenAssetStreamAsync(asset));
    }

    [Fact]
    public async Task OpenAssetStream_falls_back_to_browser_url_when_no_api_url()
    {
        var handler = new StubHttpHandler().OnBytes("/o/r/releases/download/v1/a.zip", [9]);
        using var client = TestData.Client(handler);
        var asset = new GitHubReleaseUpdater.GitHub.Models.GitHubAsset { Name = "a.zip", BrowserDownloadUrl = "https://github.com/o/r/releases/download/v1/a.zip" };

        await using var stream = await client.OpenAssetStreamAsync(asset);

        Assert.Equal(1, stream.ContentLength);
    }

    [Fact]
    public async Task OpenAssetStream_404_throws_not_found_message()
    {
        var handler = new StubHttpHandler();
        using var client = TestData.Client(handler);
        var asset = TestData.Release("v1", "a.zip").Assets[0];

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.OpenAssetStreamAsync(asset));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAssetText_rejects_null_asset()
    {
        using var client = TestData.Client(new StubHttpHandler());
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ReadAssetTextAsync(null!));
    }

    [Fact]
    public async Task Forbidden_without_rate_limit_headers_throws_forbidden_message()
    {
        var handler = new StubHttpHandler().On("/releases/latest", HttpStatusCode.Forbidden, "{\"message\":\"blocked\"}");
        using var client = TestData.Client(handler);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetLatestReleaseAsync("o", "r"));

        Assert.False(ex.IsRateLimited);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("forbidden", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocked", ex.Message);
    }

    [Fact]
    public async Task Server_error_throws_generic_message_with_status_code()
    {
        var handler = new StubHttpHandler().On("/releases/latest", HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}");
        using var client = TestData.Client(handler);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetLatestReleaseAsync("o", "r"));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("500", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Rate_limit_via_retry_after_header_when_reset_header_missing()
    {
        var handler = new StubHttpHandler().On("/releases/latest", HttpStatusCode.TooManyRequests, "{\"message\":\"secondary rate limit\"}", configure: r =>
        {
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        });
        using var client = TestData.Client(handler);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetLatestReleaseAsync("o", "r"));

        Assert.True(ex.IsRateLimited);
        Assert.NotNull(ex.RateLimitResetAt);
    }

    [Fact]
    public async Task Error_with_non_json_body_still_throws_with_generic_message()
    {
        var handler = new StubHttpHandler().On("/releases/latest", HttpStatusCode.InternalServerError, "not json", "text/plain");
        using var client = TestData.Client(handler);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetLatestReleaseAsync("o", "r"));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("500", ex.Message);
        Assert.Equal("not json", ex.ResponseBody);
    }

    [Fact]
    public async Task Timeout_throws_TimeoutException_when_request_exceeds_it()
    {
        using var client = TestData.Client(new DelayingHttpHandler(), timeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.GetLatestReleaseAsync("o", "r"));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_does_not_apply_when_null()
    {
        var handler = new StubHttpHandler().On("/releases/latest", HttpStatusCode.OK, TestData.Read("release-latest.json"));
        using var client = TestData.Client(handler, timeout: null);

        var release = await client.GetLatestReleaseAsync("o", "r");

        Assert.NotNull(release);
    }

    [Fact]
    public async Task Caller_cancellation_throws_OperationCanceledException_not_TimeoutException()
    {
        using var client = TestData.Client(new DelayingHttpHandler(), timeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLatestReleaseAsync("o", "r", cts.Token));

        Assert.IsNotType<TimeoutException>(ex);
    }
}