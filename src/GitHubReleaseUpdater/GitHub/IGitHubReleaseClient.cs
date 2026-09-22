using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.GitHub;

/// <summary>
/// Read-only access to a repository's releases and their assets.
/// </summary>
public interface IGitHubReleaseClient
{
    /// <summary>
    /// Gets the latest published, non-prerelease, non-draft release
    /// (<c>GET /repos/{owner}/{repo}/releases/latest</c>). Returns null when the repository has no such release.
    /// </summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a release by tag name. Returns null when not found.
    /// </summary>
    Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists releases, newest first (<c>GET /repos/{owner}/{repo}/releases</c>).
    /// Drafts are included only when the token has write access; callers should filter them.
    /// </summary>
    Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a stream for an asset's content. Uses the asset API endpoint with
    /// <c>Accept: application/octet-stream</c> so private repositories work when a token is configured.
    /// </summary>
    Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a stream for an asset's content starting at byte offset <paramref name="rangeStart"/>, for resuming
    /// a partial download (<c>Range: bytes={rangeStart}-</c>). Check <see cref="AssetStream.IsPartial"/> on the
    /// result: true means the server honored the range and the stream picks up at <paramref name="rangeStart"/>;
    /// false means it returned the full asset from byte 0 (no range support, or <paramref name="rangeStart"/> was 0)
    /// and any previously downloaded bytes must be discarded.
    /// </summary>
    /// <remarks>
    /// The default implementation ignores <paramref name="rangeStart"/> and always returns the full asset
    /// (<see cref="AssetStream.IsPartial"/> false), so implementations written before resume support was added
    /// keep working unchanged — they just never resume.
    /// </remarks>
    Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, long rangeStart, CancellationToken cancellationToken = default)
        => OpenAssetStreamAsync(asset, cancellationToken);

    /// <summary>
    /// Downloads a small text asset (e.g. a checksum file) fully into memory.
    /// </summary>
    Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default);
}

/// <summary>
/// A response stream together with the content length reported by the server, if any.
/// </summary>
public sealed class AssetStream : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// The underlying HTTP response (or other resource) that owns the stream's lifetime.
    /// </summary>
    private readonly IDisposable _owner;

    /// <summary>
    /// Creates a new instance; <paramref name="owner"/> is disposed together with the stream.
    /// </summary>
    /// <param name="stream">The content stream.</param>
    /// <param name="contentLength">Length of <paramref name="stream"/> itself (not the full asset when resuming), or null when unknown.</param>
    /// <param name="owner">Resource (typically an <see cref="HttpResponseMessage"/>) disposed together with the stream.</param>
    /// <param name="isPartial">True when this stream starts at <paramref name="rangeStart"/> rather than byte 0, because the server honored a range request.</param>
    /// <param name="rangeStart">The byte offset this stream starts at when <paramref name="isPartial"/> is true; otherwise 0.</param>
    /// <param name="totalLength">
    /// The full asset size if known, independent of how much of it this stream carries (e.g. from a <c>Content-Range</c>
    /// header on a partial response). Defaults to <paramref name="contentLength"/> when this is not a partial stream,
    /// since then the stream carries the whole asset.
    /// </param>
    public AssetStream(Stream stream, long? contentLength, IDisposable owner, bool isPartial = false, long rangeStart = 0, long? totalLength = null)
    {
        Stream = stream;
        ContentLength = contentLength;
        _owner = owner;
        IsPartial = isPartial;
        RangeStart = isPartial ? rangeStart : 0;
        TotalLength = totalLength ?? (isPartial ? null : contentLength);
    }

    /// <summary>
    /// Content length from the response headers, or null when unknown.
    /// </summary>
    public long? ContentLength
    {
        get;
    }

    /// <summary>
    /// True when the server honored a range request and this stream starts at <see cref="RangeStart"/> rather
    /// than the beginning of the asset.
    /// </summary>
    public bool IsPartial { get; }

    /// <summary>
    /// The byte offset this stream starts at, or 0 when <see cref="IsPartial"/> is false.
    /// </summary>
    public long RangeStart { get; }

    /// <summary>
    /// The full asset size if known, regardless of how much of it this stream carries. Null when unknown.
    /// </summary>
    public long? TotalLength { get; }

    /// <summary>
    /// The content stream.
    /// </summary>
    public Stream Stream
    {
        get;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stream.Dispose();
        _owner.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        _owner.Dispose();
    }
}