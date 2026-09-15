using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.Verification;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater;

/// <summary>Configuration for <see cref="ReleaseUpdater"/>.</summary>
public sealed class UpdaterOptions
{
    /// <summary>Repository owner (user or organization).</summary>
    public required string Owner { get; init; }

    /// <summary>Repository name.</summary>
    public required string Repo { get; init; }

    /// <summary>Version currently installed. Compared against the latest release.</summary>
    public required SemanticVersion CurrentVersion { get; init; }

    /// <summary>
    /// API base URL. Null uses <c>https://api.github.com/</c>. For GitHub Enterprise Server use <c>https://HOST/api/v3/</c>.
    /// </summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>Optional token for private repositories and higher rate limits.</summary>
    public string? Token { get; init; }

    /// <summary>User-Agent sent to GitHub. Defaults to <c>GitHubReleaseUpdater</c>.</summary>
    public string? UserAgent { get; init; }

    /// <summary>When true, pre-releases are considered as update candidates.</summary>
    public bool IncludePrerelease { get; init; }

    /// <summary>Prefix to strip from tag names before parsing (a plain <c>v</c> is always handled), e.g. <c>release-</c>.</summary>
    public string? TagPrefix { get; init; }

    /// <summary>Number of releases to inspect when <see cref="IncludePrerelease"/> is true (1–100).</summary>
    public int ReleaseScanCount { get; init; } = 30;

    /// <summary>Chooses the asset to download. Defaults to <see cref="RuntimeAssetSelector"/>.</summary>
    public IAssetSelector? AssetSelector { get; init; }

    /// <summary>
    /// Supplies expected checksums. Defaults to <see cref="ReleaseChecksumProvider"/>.
    /// Set to <see cref="NoChecksumProvider"/> to disable verification entirely.
    /// </summary>
    public IChecksumProvider? ChecksumProvider { get; init; }

    /// <summary>When true, a download with no available checksum fails instead of being reported as unverified. Default false.</summary>
    public bool RequireChecksum { get; init; }

    /// <summary>Optional shared <see cref="HttpClient"/>.</summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>A provider that never yields a checksum (disables verification).</summary>
public sealed class NoChecksumProvider : IChecksumProvider
{
    /// <summary>Shared instance.</summary>
    public static NoChecksumProvider Instance { get; } = new();

    /// <inheritdoc />
    public Task<string?> GetExpectedSha256Async(GitHub.Models.GitHubRelease release, GitHub.Models.GitHubAsset asset, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
