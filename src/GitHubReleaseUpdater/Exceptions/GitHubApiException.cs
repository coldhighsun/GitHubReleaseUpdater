using System.Net;

namespace GitHubReleaseUpdater.Exceptions;

/// <summary>
/// Raised when the GitHub API returns an unsuccessful response.
/// </summary>
public sealed class GitHubApiException : UpdaterException
{
    /// <summary>
    /// HTTP status code returned by GitHub.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// True when the request was rejected due to rate limiting.
    /// </summary>
    public bool IsRateLimited { get; }

    /// <summary>
    /// When the rate limit resets, if the response carried <c>X-RateLimit-Reset</c>.
    /// </summary>
    public DateTimeOffset? RateLimitResetAt { get; }

    /// <summary>
    /// Raw response body (may be empty).
    /// </summary>
    public string ResponseBody { get; }

    /// <inheritdoc />
    public GitHubApiException() : this("GitHub API request failed.", HttpStatusCode.InternalServerError, false, null, string.Empty) { }
    /// <inheritdoc />
    public GitHubApiException(string message) : this(message, HttpStatusCode.InternalServerError, false, null, string.Empty) { }
    /// <inheritdoc />
    public GitHubApiException(string message, Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = HttpStatusCode.InternalServerError;
        ResponseBody = string.Empty;
    }

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public GitHubApiException(string message, HttpStatusCode statusCode, bool isRateLimited, DateTimeOffset? rateLimitResetAt, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        IsRateLimited = isRateLimited;
        RateLimitResetAt = rateLimitResetAt;
        ResponseBody = responseBody;
    }
}
