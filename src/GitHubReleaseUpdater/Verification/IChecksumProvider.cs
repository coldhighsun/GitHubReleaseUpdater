using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Verification;

/// <summary>Supplies the expected SHA-256 digest for a release asset.</summary>
public interface IChecksumProvider
{
    /// <summary>
    /// Returns the expected lowercase/uppercase hex SHA-256 for <paramref name="asset"/>,
    /// or null when no checksum is available (verification is then skipped).
    /// </summary>
    Task<string?> GetExpectedSha256Async(GitHubRelease release, GitHubAsset asset, CancellationToken cancellationToken = default);
}

/// <summary>Returns a fixed digest supplied by the caller.</summary>
public sealed class StaticChecksumProvider : IChecksumProvider
{
    private readonly string _sha256;

    /// <summary>Creates a provider with a known hex SHA-256 (with or without a <c>sha256:</c> prefix).</summary>
    public StaticChecksumProvider(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        _sha256 = ChecksumParser.NormalizeDigest(sha256)
                  ?? throw new ArgumentException("Value is not a valid SHA-256 hex digest.", nameof(sha256));
    }

    /// <inheritdoc />
    public Task<string?> GetExpectedSha256Async(GitHubRelease release, GitHubAsset asset, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(_sha256);
}

/// <summary>Chains providers; the first non-null result wins.</summary>
public sealed class CompositeChecksumProvider : IChecksumProvider
{
    private readonly IReadOnlyList<IChecksumProvider> _providers;

    /// <summary>Creates a composite over the given providers, in priority order.</summary>
    public CompositeChecksumProvider(params IChecksumProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
    }

    /// <inheritdoc />
    public async Task<string?> GetExpectedSha256Async(GitHubRelease release, GitHubAsset asset, CancellationToken cancellationToken = default)
    {
        foreach (var p in _providers)
        {
            var result = await p.GetExpectedSha256Async(release, asset, cancellationToken).ConfigureAwait(false);
            if (result is not null) return result;
        }
        return null;
    }
}
