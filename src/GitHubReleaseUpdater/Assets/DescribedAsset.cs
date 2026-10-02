using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Assets;

/// <summary>
/// A release asset together with the platform detected from its file name, so a caller can show the
/// available downloads grouped by platform and let the user choose one.
/// </summary>
/// <param name="Asset">The release asset.</param>
/// <param name="Platform">The OS/architecture its file name targets.</param>
/// <param name="IsMetadata">True for checksum, signature and SBOM sidecar files, which are not meant to be installed.</param>
/// <param name="MatchesCurrent">
/// True when the asset names the current OS/architecture (or only one of them, with no conflict on the other).
/// Always false for metadata files and for assets whose name carries no platform.
/// </param>
public sealed record DescribedAsset(GitHubAsset Asset, AssetPlatform Platform, bool IsMetadata, bool MatchesCurrent);
