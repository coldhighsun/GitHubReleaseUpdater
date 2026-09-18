using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater;

/// <summary>
/// An update found to be newer than the caller's current version. Unlike the top-level nullable
/// <see cref="UpdateCheckResult.LatestVersion"/>/<see cref="UpdateCheckResult.Release"/>, <see cref="Version"/>
/// and <see cref="Release"/> here are guaranteed non-null whenever an <see cref="AvailableUpdate"/> exists.
/// </summary>
public sealed class AvailableUpdate
{
    internal AvailableUpdate(SemanticVersion version, GitHubRelease release, GitHubAsset? selectedAsset)
    {
        Version = version;
        Release = release;
        SelectedAsset = selectedAsset;
    }

    /// <summary>
    /// The release corresponding to <see cref="Version"/>.
    /// </summary>
    public GitHubRelease Release { get; }

    /// <summary>
    /// Asset chosen by the configured selector, or null when none matched.
    /// </summary>
    public GitHubAsset? SelectedAsset { get; }

    /// <summary>
    /// The newer version.
    /// </summary>
    public SemanticVersion Version { get; }
}

/// <summary>
/// Outcome of <see cref="ReleaseUpdater.CheckForUpdateAsync"/>.
/// </summary>
public sealed class UpdateCheckResult
{
    internal UpdateCheckResult(SemanticVersion current, SemanticVersion? latest, GitHubRelease? release, GitHubAsset? asset, IReadOnlyList<string> skippedTags, bool isUpdateAvailable)
    {
        CurrentVersion = current;
        LatestVersion = latest;
        Release = release;
        SkippedTags = skippedTags;
        Update = isUpdateAvailable && latest is not null && release is not null
            ? new AvailableUpdate(latest, release, asset)
            : null;
    }

    private UpdateCheckResult(SemanticVersion current, bool throttled)
    {
        CurrentVersion = current;
        SkippedTags = [];
        Throttled = throttled;
    }

    /// <summary>
    /// The version the caller is running.
    /// </summary>
    public SemanticVersion CurrentVersion { get; }

    /// <summary>
    /// True when <see cref="Update"/> is non-null, i.e. a release newer than <see cref="CurrentVersion"/>
    /// was found (and, if a <c>LastCheckStore</c> is configured, was not explicitly skipped).
    /// </summary>
    public bool IsUpdateAvailable => Update is not null;

    /// <summary>
    /// Highest version found, or null when the repository has no parseable release. Populated even when
    /// <see cref="IsUpdateAvailable"/> is false (e.g. it is not newer than <see cref="CurrentVersion"/>, or
    /// it was explicitly skipped) — use <see cref="Update"/> instead when you only care about an actionable
    /// update, since its members are guaranteed non-null.
    /// </summary>
    public SemanticVersion? LatestVersion { get; }

    /// <summary>
    /// The release corresponding to <see cref="LatestVersion"/>. Same non-null contract as <see cref="LatestVersion"/>.
    /// </summary>
    public GitHubRelease? Release { get; }

    /// <summary>
    /// Release notes of <see cref="Release"/>, if any.
    /// </summary>
    public string? ReleaseNotes => Release?.Body;

    /// <summary>
    /// Asset chosen by the configured selector for <see cref="Update"/> (null when none matched or no update).
    /// </summary>
    public GitHubAsset? SelectedAsset => Update?.SelectedAsset;

    /// <summary>
    /// Tags that were skipped because they could not be parsed as versions.
    /// </summary>
    public IReadOnlyList<string> SkippedTags { get; }

    /// <summary>
    /// True when this result was returned without contacting GitHub because a check ran more recently than
    /// <c>UpdaterOptions.MinimumCheckInterval</c> allows. All other members reflect "nothing changed since
    /// the last check" rather than any freshly observed release state.
    /// </summary>
    public bool Throttled { get; }

    /// <summary>
    /// The actionable update, or null when there is none. Non-null exactly when <see cref="IsUpdateAvailable"/>
    /// is true, with <see cref="AvailableUpdate.Version"/> and <see cref="AvailableUpdate.Release"/> guaranteed
    /// non-null — prefer this over <see cref="LatestVersion"/>/<see cref="Release"/> to avoid defensive null
    /// checks when you only act on a real update.
    /// </summary>
    public AvailableUpdate? Update { get; }

    /// <summary>
    /// Builds a result reporting that no check was performed because it was too soon since the last one.
    /// </summary>
    internal static UpdateCheckResult ThrottledResult(SemanticVersion current) => new(current, throttled: true);
}

/// <summary>
/// Outcome of <c>ReleaseUpdater.DownloadAsync</c>.
/// </summary>
public sealed class DownloadResult
{
    internal DownloadResult(string filePath, GitHubAsset asset, string sha256, bool verified)
    {
        FilePath = filePath;
        Asset = asset;
        Sha256 = sha256;
        Verified = verified;
    }

    /// <summary>
    /// The asset that was downloaded.
    /// </summary>
    public GitHubAsset Asset { get; }

    /// <summary>
    /// Full path of the downloaded file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// SHA-256 of the file on disk (lowercase hex).
    /// </summary>
    public string Sha256 { get; }

    /// <summary>
    /// True when the hash was compared against an expected value and matched; false when no checksum was available.
    /// </summary>
    public bool Verified { get; }
}