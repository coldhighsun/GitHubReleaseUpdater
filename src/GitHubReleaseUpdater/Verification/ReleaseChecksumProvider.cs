using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Verification;

/// <summary>
/// Resolves the expected SHA-256 from the release itself, in order:
/// <list type="number">
/// <item>a sidecar asset named <c>&lt;asset&gt;.sha256</c></item>
/// <item>an aggregate sums file (<c>SHA256SUMS</c>, <c>SHA256SUMS.txt</c>, <c>checksums.txt</c>, <c>*_checksums.txt</c>, <c>sha256sum.txt</c>…)</item>
/// <item>the <c>digest</c> field GitHub reports for the asset</item>
/// </list>
/// </summary>
public sealed class ReleaseChecksumProvider : IChecksumProvider
{
    private static readonly string[] AggregateNames =
    [
        "sha256sums", "sha256sums.txt", "sha256sum.txt", "sha256sum", "checksums.txt", "checksums", "checksums.sha256", "sha256.txt",
    ];

    private readonly IGitHubReleaseClient _client;

    /// <summary>Creates a provider that reads sidecar/aggregate files through <paramref name="client"/>.</summary>
    public ReleaseChecksumProvider(IGitHubReleaseClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task<string?> GetExpectedSha256Async(GitHubRelease release, GitHubAsset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);

        // 1. <asset>.sha256 sidecar
        var sidecar = release.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (sidecar is not null)
        {
            var text = await _client.ReadAssetTextAsync(sidecar, cancellationToken).ConfigureAwait(false);
            var found = ChecksumParser.FindFor(ChecksumParser.ParseSums(text), asset.Name);
            if (found is not null) return found;
        }

        // 2. aggregate sums file(s)
        foreach (var candidate in release.Assets.Where(a => IsAggregate(a.Name)))
        {
            var text = await _client.ReadAssetTextAsync(candidate, cancellationToken).ConfigureAwait(false);
            var sums = ChecksumParser.ParseSums(text);
            if (sums.TryGetValue(asset.Name, out var h)) return h;
        }

        // 3. GitHub-reported digest
        return ChecksumParser.NormalizeDigest(asset.Digest);
    }

    /// <summary>True when the name looks like an aggregate SHA-256 sums file.</summary>
    public static bool IsAggregate(string name)
    {
        var lower = name.ToLowerInvariant();
        if (AggregateNames.Contains(lower)) return true;
        // e.g. myapp_1.2.3_checksums.txt, myapp-SHA256SUMS.txt
        return (lower.EndsWith("checksums.txt", StringComparison.Ordinal) || lower.EndsWith("sha256sums.txt", StringComparison.Ordinal) || lower.EndsWith("sha256sums", StringComparison.Ordinal))
               && !lower.Contains("sha512", StringComparison.Ordinal);
    }
}
