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
    /// Default per-request timeout applied when a caller constructs <see cref="GitHubReleaseClient"/> directly
    /// without specifying <c>timeout</c>, matching <see cref="UpdaterOptions.Timeout"/>'s default.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

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
    /// Process-wide <see cref="HttpClient"/> shared by every <see cref="GitHubReleaseClient"/> created without
    /// an explicit <c>httpClient</c> argument, so that constructing many clients (or many <see cref="ReleaseUpdater"/>
    /// instances) never creates more than one connection pool. All per-request state (Accept, User-Agent,
    /// Authorization) is set on the <see cref="HttpRequestMessage"/> rather than on this client, so sharing it
    /// across instances with different tokens/base URLs is safe.
    /// </summary>
    private static readonly Lazy<HttpClient> SharedHttpClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }));

    /// <summary>
    /// Optional bearer token sent with every request.
    /// </summary>
    private readonly string? _token;

    /// <summary>
    /// User-Agent header value sent with every request.
    /// </summary>
    private readonly string _userAgent;

    /// <summary>
    /// Per-request timeout applied on top of <see cref="_http"/>'s own timeout, or null to rely on the
    /// latter alone.
    /// </summary>
    private readonly TimeSpan? _timeout;

    /// <summary>
    /// Creates a client.
    /// </summary>
    /// <param name="baseUrl">API base, e.g. <c>https://api.github.com/</c> or <c>https://ghe.example.com/api/v3/</c>. Null uses github.com.</param>
    /// <param name="token">Optional personal access / fine-grained / app token.</param>
    /// <param name="userAgent">User-Agent header (GitHub rejects requests without one).</param>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> to use instead of the library's <see cref="SharedHttpClient"/> (e.g. to
    /// supply one from <c>IHttpClientFactory</c> or a test handler). This instance never disposes it.
    /// </param>
    /// <remarks>
    /// Defaults <c>timeout</c> to <see cref="DefaultTimeout"/>. To disable the per-request timeout entirely,
    /// use the overload that takes an explicit <c>timeout</c> and pass <see langword="null"/>.
    /// </remarks>
    public GitHubReleaseClient(Uri? baseUrl = null, string? token = null, string? userAgent = null, HttpClient? httpClient = null)
        : this(baseUrl, token, userAgent, httpClient, DefaultTimeout)
    {
    }

    /// <summary>
    /// Creates a client with an explicit per-request timeout.
    /// </summary>
    /// <param name="baseUrl">API base, e.g. <c>https://api.github.com/</c> or <c>https://ghe.example.com/api/v3/</c>. Null uses github.com.</param>
    /// <param name="token">Optional personal access / fine-grained / app token.</param>
    /// <param name="userAgent">User-Agent header (GitHub rejects requests without one).</param>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> to use instead of the library's <see cref="SharedHttpClient"/> (e.g. to
    /// supply one from <c>IHttpClientFactory</c> or a test handler). This instance never disposes it.
    /// </param>
    /// <param name="timeout">
    /// Per-request timeout, or <see langword="null"/> to disable it and rely solely on <paramref name="httpClient"/>'s
    /// own timeout. On expiry a <see cref="TimeoutException"/> is thrown instead of an
    /// <see cref="OperationCanceledException"/> tied to the caller's cancellation token.
    /// </param>
    public GitHubReleaseClient(Uri? baseUrl, string? token, string? userAgent, HttpClient? httpClient, TimeSpan? timeout)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl ?? DefaultBaseUrl);
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "GitHubReleaseUpdater" : userAgent;
        _http = httpClient ?? SharedHttpClient.Value;
        _timeout = timeout;
    }

    /// <summary>
    /// No-op: this instance never owns <see cref="_http"/> — a caller-supplied client is the caller's to
    /// dispose, and the default <see cref="SharedHttpClient"/> is process-wide and outlives any one instance.
    /// </summary>
    public void Dispose()
    {
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
    public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
        => OpenAssetStreamAsync(asset, rangeStart: 0, cancellationToken);

    /// <inheritdoc />
    public async Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, long rangeStart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(rangeStart);
        var request = CreateAssetRequest(asset, rangeStart);
        var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, allowNotFound: false, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var isPartial = rangeStart > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            var totalLength = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
            return new AssetStream(stream, response.Content.Headers.ContentLength, response, isPartial, rangeStart, totalLength);
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
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, allowNotFound: false, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request honoring <see cref="_timeout"/> when set, translating an internally-triggered
    /// cancellation into <see cref="TimeoutException"/> so it is distinguishable from the caller cancelling
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        using var scope = new TimeoutScope(_timeout, cancellationToken);
        try
        {
            return await _http.SendAsync(request, completionOption, scope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scope.IsTimeout)
        {
            throw new TimeoutException($"GitHub request to '{request.RequestUri}' timed out after {scope.Timeout}.");
        }
    }

    /// <summary>
    /// Bounds a scope of work to <see cref="_timeout"/> via a dedicated, own timer-driven <see cref="CancellationTokenSource"/>
    /// linked with the caller's token. <see cref="IsTimeout"/> reports whether that dedicated timer (not the caller's
    /// token) triggered the cancellation, so classification does not race against the caller cancelling around the
    /// same moment the timeout elapses.
    /// </summary>
    private readonly struct TimeoutScope : IDisposable
    {
        private readonly CancellationTokenSource? _timeoutCts;
        private readonly CancellationTokenSource? _linkedCts;

        public TimeoutScope(TimeSpan? timeout, CancellationToken cancellationToken)
        {
            Timeout = timeout;
            if (timeout is not { } value)
            {
                Token = cancellationToken;
                return;
            }
            _timeoutCts = new CancellationTokenSource(value);
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeoutCts.Token);
            Token = _linkedCts.Token;
        }

        public TimeSpan? Timeout { get; }

        public CancellationToken Token { get; }

        public bool IsTimeout => _timeoutCts?.IsCancellationRequested ?? false;

        public void Dispose()
        {
            _linkedCts?.Dispose();
            _timeoutCts?.Dispose();
        }
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
    /// Builds a GET request for an asset's download URL, preferring the API URL over the browser URL, with an
    /// optional <c>Range: bytes={rangeStart}-</c> header to resume a partial download.
    /// </summary>
    private HttpRequestMessage CreateAssetRequest(GitHubAsset asset, long rangeStart = 0)
    {
        // Prefer the API endpoint (works for private repos with a token); fall back to the browser URL.
        var url = !string.IsNullOrEmpty(asset.ApiUrl) ? asset.ApiUrl : asset.BrowserDownloadUrl;
        if (string.IsNullOrEmpty(url))
            throw new ArgumentException("Asset has no download URL.", nameof(asset));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(request, OctetStreamAccept);
        if (rangeStart > 0)
            request.Headers.Range = new RangeHeaderValue(rangeStart, null);
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

        // The whole call — headers, body and deserialization — is bounded by _timeout here, unlike the
        // header-only SendAsync helper used for asset downloads (see its own doc comment).
        using var scope = new TimeoutScope(_timeout, cancellationToken);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.Token).ConfigureAwait(false);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(response, allowNotFound, scope.Token).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(scope.Token).ConfigureAwait(false);
            try
            {
                return await JsonSerializer.DeserializeAsync(stream, typeInfo, scope.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new GitHubApiException($"Failed to parse GitHub API response from {url}.", ex);
            }
        }
        catch (OperationCanceledException) when (scope.IsTimeout)
        {
            throw new TimeoutException($"GitHub request to '{url}' timed out after {scope.Timeout}.");
        }
    }
}
