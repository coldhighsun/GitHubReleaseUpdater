using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater;

/// <summary>
/// Outcome of <see cref="ReleaseUpdater.CheckForUpdateAsync"/>.
/// </summary>
public sealed class UpdateCheckResult
{
    /// <summary>
    /// True when <see cref="LatestVersion"/> is newer than <see cref="CurrentVersion"/>.
    /// </summary>
    public bool IsUpdateAvailable { get; }

    /// <summary>
    /// The version the caller is running.
    /// </summary>
    public SemanticVersion CurrentVersion { get; }

    /// <summary>
    /// Highest version found, or null when the repository has no parseable release.
    /// </summary>
    public SemanticVersion? LatestVersion { get; }

    /// <summary>
    /// The release corresponding to <see cref="LatestVersion"/>.
    /// </summary>
    public GitHubRelease? Release { get; }

    /// <summary>
    /// Asset chosen by the configured selector (null when none matched or no update).
    /// </summary>
    public GitHubAsset? SelectedAsset { get; }

    /// <summary>
    /// Tags that were skipped because they could not be parsed as versions.
    /// </summary>
    public IReadOnlyList<string> SkippedTags { get; }

    /// <summary>
    /// Release notes of <see cref="Release"/>, if any.
    /// </summary>
    public string? ReleaseNotes => Release?.Body;

    internal UpdateCheckResult(SemanticVersion current, SemanticVersion? latest, GitHubRelease? release, GitHubAsset? asset, IReadOnlyList<string> skippedTags)
    {
        CurrentVersion = current;
        LatestVersion = latest;
        Release = release;
        SelectedAsset = asset;
        SkippedTags = skippedTags;
        IsUpdateAvailable = latest is not null && latest > current;
    }
}

/// <summary>
/// Outcome of <c>ReleaseUpdater.DownloadAsync</c>.
/// </summary>
public sealed class DownloadResult
{
    /// <summary>
    /// Full path of the downloaded file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// The asset that was downloaded.
    /// </summary>
    public GitHubAsset Asset { get; }

    /// <summary>
    /// SHA-256 of the file on disk (lowercase hex).
    /// </summary>
    public string Sha256 { get; }

    /// <summary>
    /// True when the hash was compared against an expected value and matched; false when no checksum was available.
    /// </summary>
    public bool Verified { get; }

    internal DownloadResult(string filePath, GitHubAsset asset, string sha256, bool verified)
    {
        FilePath = filePath;
        Asset = asset;
        Sha256 = sha256;
        Verified = verified;
    }
}
