using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.GitHub;

/// <summary>Read-only access to a repository's releases and their assets.</summary>
public interface IGitHubReleaseClient
{
    /// <summary>
    /// Gets the latest published, non-prerelease, non-draft release
    /// (<c>GET /repos/{owner}/{repo}/releases/latest</c>). Returns null when the repository has no such release.
    /// </summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists releases, newest first (<c>GET /repos/{owner}/{repo}/releases</c>).
    /// Drafts are included only when the token has write access; callers should filter them.
    /// </summary>
    Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default);

    /// <summary>Gets a release by tag name. Returns null when not found.</summary>
    Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a stream for an asset's content. Uses the asset API endpoint with
    /// <c>Accept: application/octet-stream</c> so private repositories work when a token is configured.
    /// </summary>
    Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default);

    /// <summary>Downloads a small text asset (e.g. a checksum file) fully into memory.</summary>
    Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default);
}

/// <summary>A response stream together with the content length reported by the server, if any.</summary>
public sealed class AssetStream : IAsyncDisposable, IDisposable
{
    private readonly IDisposable _owner;

    /// <summary>The content stream.</summary>
    public Stream Stream { get; }

    /// <summary>Content length from the response headers, or null when unknown.</summary>
    public long? ContentLength { get; }

    /// <summary>Creates a new instance; <paramref name="owner"/> is disposed together with the stream.</summary>
    public AssetStream(Stream stream, long? contentLength, IDisposable owner)
    {
        Stream = stream;
        ContentLength = contentLength;
        _owner = owner;
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
