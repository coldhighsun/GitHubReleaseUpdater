using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub.Models;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GitHubReleaseUpdater.GitHub;

/// <summary>
/// <see cref="IGitHubReleaseClient"/> backed by <see cref="HttpClient"/>.
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient, IDisposable
{
    /// <summary>
    /// Default API base for github.com.
    /// </summary>
    public static readonly Uri DefaultBaseUrl = new("https://api.github.com/");

    /// <summary>
    /// GitHub REST API version sent via the <c>X-GitHub-Api-Version</c> header.
    /// </summary>
    private const string ApiVersion = "2022-11-28";

    /// <summary>
    /// Accept header used for JSON API requests.
    /// </summary>
    private const string JsonAccept = "application/vnd.github+json";

    /// <summary>
    /// Accept header used for raw asset downloads.
    /// </summary>
    private const string OctetStreamAccept = "application/octet-stream";

    /// <summary>
    /// Normalized (trailing-slash) API base URL.
    /// </summary>
    private readonly Uri _baseUrl;

    /// <summary>
    /// The underlying HTTP client used for all requests.
    /// </summary>
    private readonly HttpClient _http;

    /// <summary>
    /// True when this instance created <see cref="_http"/> and is responsible for disposing it.
    /// </summary>
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Optional bearer token sent with every request.
    /// </summary>
    private readonly string? _token;

    /// <summary>
    /// User-Agent header value sent with every request.
    /// </summary>
    private readonly string _userAgent;

    /// <summary>
    /// Creates a client.
    /// </summary>
    /// <param name="baseUrl">API base, e.g. <c>https://api.github.com/</c> or <c>https://ghe.example.com/api/v3/</c>. Null uses github.com.</param>
    /// <param name="token">Optional personal access / fine-grained / app token.</param>
    /// <param name="userAgent">User-Agent header (GitHub rejects requests without one).</param>
    /// <param name="httpClient">Optional shared <see cref="HttpClient"/>; when omitted one is created and owned by this instance.</param>
    public GitHubReleaseClient(Uri? baseUrl = null, string? token = null, string? userAgent = null, HttpClient? httpClient = null)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl ?? DefaultBaseUrl);
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "GitHubReleaseUpdater" : userAgent;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    /// <inheritdoc />
    public async Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default)
    {
        ValidateRepo(owner, repo);
        var url = new Uri(_baseUrl, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest");
        return await GetJsonAsync(url, JsonContext.Default.GitHubRelease, allowNotFound: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default)
    {
        ValidateRepo(owner, repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var url = new Uri(_baseUrl, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/tags/{Uri.EscapeDataString(tag)}");
        return await GetJsonAsync(url, JsonContext.Default.GitHubRelease, allowNotFound: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default)
    {
        ValidateRepo(owner, repo);
        ArgumentOutOfRangeException.ThrowIfLessThan(perPage, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(perPage, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        var url = new Uri(_baseUrl, string.Create(CultureInfo.InvariantCulture,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page={perPage}&page={page}"));
        var list = await GetJsonAsync(url, JsonContext.Default.ListGitHubRelease, allowNotFound: false, cancellationToken).ConfigureAwait(false);
        return list ?? [];
    }

    /// <inheritdoc />
    public async Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var request = CreateAssetRequest(asset);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, allowNotFound: false, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new AssetStream(stream, response.Content.Headers.ContentLength, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        using var request = CreateAssetRequest(asset);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, allowNotFound: false, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="GitHubApiException"/> with a diagnostic message when the response is not successful
    /// (and not an allowed 404), classifying rate-limit and auth failures along the way.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, bool allowNotFound, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return;

        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) { /* body is optional for diagnostics */ }
        catch (IOException) { }

        string? apiMessage = null;
        if (body.Length > 0)
        {
            try
            {
                apiMessage = JsonSerializer.Deserialize(body, JsonContext.Default.GitHubErrorResponse)?.Message;
            }
            catch (JsonException) { }
        }

        var remaining = GetHeader(response, "X-RateLimit-Remaining");
        var isRateLimited = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                            && (remaining == "0" || (apiMessage?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ?? false));

        DateTimeOffset? resetAt = null;
        if (long.TryParse(GetHeader(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            resetAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
        else if (response.Headers.RetryAfter?.Delta is { } delta)
            resetAt = DateTimeOffset.UtcNow + delta;

        var message = response.StatusCode switch
        {
            _ when isRateLimited => $"GitHub API rate limit exceeded{(resetAt is null ? "" : $"; resets at {resetAt:u}")}. Provide a token to raise the limit.",
            HttpStatusCode.Unauthorized => "GitHub API rejected the credentials (401). Check the token.",
            HttpStatusCode.Forbidden => $"GitHub API access forbidden (403){FormatApi(apiMessage)}.",
            HttpStatusCode.NotFound => $"Repository or release not found (404){FormatApi(apiMessage)}. For private repositories a token is required.",
            _ => $"GitHub API request failed with {(int)response.StatusCode} {response.ReasonPhrase}{FormatApi(apiMessage)}.",
        };

        throw new GitHubApiException(message, response.StatusCode, isRateLimited, resetAt, body);

        static string FormatApi(string? m) => string.IsNullOrEmpty(m) ? string.Empty : $": {m}";
    }

    /// <summary>
    /// Returns the first value of a response header, or null when absent.
    /// </summary>
    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Ensures the base URL is absolute and ends with a trailing slash so relative paths combine correctly.
    /// </summary>
    private static Uri NormalizeBaseUrl(Uri baseUrl)
    {
        if (!baseUrl.IsAbsoluteUri)
            throw new ArgumentException("Base URL must be absolute.", nameof(baseUrl));
        var text = baseUrl.ToString();
        return text.EndsWith('/') ? baseUrl : new Uri(text + "/");
    }

    /// <summary>
    /// Validates that owner and repo names are non-empty.
    /// </summary>
    private static void ValidateRepo(string owner, string repo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
    }

    /// <summary>
    /// Sets the Accept, User-Agent, API version and (when configured) bearer token headers on a request.
    /// </summary>
    private void ApplyCommonHeaders(HttpRequestMessage request, string accept)
    {
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        if (_token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    /// <summary>
    /// Builds a GET request for an asset's download URL, preferring the API URL over the browser URL.
    /// </summary>
    private HttpRequestMessage CreateAssetRequest(GitHubAsset asset)
    {
        // Prefer the API endpoint (works for private repos with a token); fall back to the browser URL.
        var url = !string.IsNullOrEmpty(asset.ApiUrl) ? asset.ApiUrl : asset.BrowserDownloadUrl;
        if (string.IsNullOrEmpty(url))
            throw new ArgumentException("Asset has no download URL.", nameof(asset));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(request, OctetStreamAccept);
        return request;
    }

    /// <summary>
    /// Sends a GET request and deserializes the JSON response body, returning null on a 404 when allowed.
    /// </summary>
    private async Task<T?> GetJsonAsync<T>(Uri url, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, bool allowNotFound, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(request, JsonAccept);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, allowNotFound, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException($"Failed to parse GitHub API response from {url}.", ex);
        }
    }
}
