using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.LastCheck;
using GitHubReleaseUpdater.Verification;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater;

/// <summary>
/// Configuration for <see cref="ReleaseUpdater"/>.
/// </summary>
public sealed class UpdaterOptions
{
    /// <summary>
    /// Chooses the asset to download. Defaults to <see cref="RuntimeAssetSelector"/>.
    /// </summary>
    public IAssetSelector? AssetSelector { get; init; }

    /// <summary>
    /// API base URL. Null uses <c>https://api.github.com/</c>. For GitHub Enterprise Server use <c>https://HOST/api/v3/</c>.
    /// </summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>
    /// Supplies expected checksums. Defaults to <see cref="ReleaseChecksumProvider"/>.
    /// Set to <see cref="NoChecksumProvider"/> to disable verification entirely.
    /// </summary>
    public IChecksumProvider? ChecksumProvider { get; init; }

    /// <summary>
    /// Version currently installed. Compared against the latest release.
    /// </summary>
    public required SemanticVersion CurrentVersion { get; init; }

    /// <summary>
    /// Optional shared <see cref="HttpClient"/>.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// When true, pre-releases are considered as update candidates.
    /// </summary>
    public bool IncludePrerelease { get; init; }

    /// <summary>
    /// Optional store letting <see cref="ReleaseUpdater.CheckForUpdateAsync"/> throttle checks to
    /// <see cref="MinimumCheckInterval"/> and suppress a version the caller marked as skipped. Left null by
    /// default: the library then does no throttling or skip filtering and leaves that policy to the caller,
    /// as before. See <see cref="ILastCheckStore"/>.
    /// </summary>
    public ILastCheckStore? LastCheckStore { get; init; }

    /// <summary>
    /// Minimum time that must pass since <see cref="LastCheckStore"/> recorded a check before
    /// <see cref="ReleaseUpdater.CheckForUpdateAsync"/> contacts GitHub again; a call inside that window
    /// returns a <see cref="UpdateCheckResult.Throttled"/> result instead. Ignored when
    /// <see cref="LastCheckStore"/> is null.
    /// </summary>
    public TimeSpan? MinimumCheckInterval { get; init; }

    /// <summary>
    /// Extra attempts <see cref="ReleaseUpdater.DownloadAsync(UpdateCheckResult, string, IProgress{Download.DownloadProgress}?, CancellationToken)"/>
    /// makes after a failed download before giving up. A transient failure (network I/O error, request timeout,
    /// or a truncated body) is retried with exponential backoff starting at <see cref="DownloadRetryDelay"/>; a
    /// checksum mismatch, disk error, or caller cancellation is never retried. Default 2 (3 attempts total).
    /// </summary>
    public int DownloadMaxRetryAttempts { get; init; } = 2;

    /// <summary>
    /// Delay before the first download retry; each subsequent retry doubles it.
    /// </summary>
    public TimeSpan DownloadRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When true (the default), a partially downloaded file left on disk — from an earlier retry or from a
    /// previous call that never got to clean up (e.g. the process was killed) — is resumed via an HTTP range
    /// request instead of re-downloaded from byte 0. See <see cref="Download.AssetDownloader.AllowResume"/>.
    /// </summary>
    public bool DownloadAllowResume { get; init; } = true;

    /// <summary>
    /// Repository owner (user or organization).
    /// </summary>
    public required string Owner { get; init; }

    /// <summary>
    /// Number of releases to inspect when <see cref="IncludePrerelease"/> is true (1–100).
    /// </summary>
    public int ReleaseScanCount { get; init; } = 30;

    /// <summary>
    /// Repository name.
    /// </summary>
    public required string Repo { get; init; }

    /// <summary>
    /// Per-request timeout for GitHub API calls and asset downloads. Defaults to 10 seconds; set to null to
    /// rely solely on the underlying <see cref="HttpClient"/>'s own timeout (100 seconds by default) instead.
    /// On expiry a <see cref="TimeoutException"/> is thrown rather than an <see cref="OperationCanceledException"/>
    /// tied to the caller's cancellation token, so it is distinguishable from caller-requested cancellation. For
    /// asset downloads this only bounds the time to receive response headers, not the full transfer; see
    /// <see cref="DownloadIdleTimeout"/> for stalls mid-transfer. Must be at least 1 millisecond (or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>); <see cref="ReleaseUpdater"/> throws
    /// <see cref="ArgumentOutOfRangeException"/> otherwise.
    /// </summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Longest an asset download may go without receiving any data before it is treated as stalled and fails with
    /// <see cref="TimeoutException"/>, which is retried (resuming from the last checkpoint) like any other transient
    /// failure. The timer restarts on every chunk received, so this bounds idle time, not total transfer time.
    /// Defaults to 30 seconds; set to null to wait indefinitely. Must be at least 1 millisecond (or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>). See <see cref="Download.AssetDownloader.IdleTimeout"/>.
    /// </summary>
    public TimeSpan? DownloadIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, a download with no available checksum fails instead of being reported as unverified. Default false.
    /// </summary>
    public bool RequireChecksum { get; init; }

    /// <summary>
    /// Prefix to strip from tag names before parsing (a plain <c>v</c> is always handled), e.g. <c>release-</c>.
    /// </summary>
    public string? TagPrefix { get; init; }

    /// <summary>
    /// Optional token for private repositories and higher rate limits.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// User-Agent sent to GitHub. Defaults to <c>GitHubReleaseUpdater</c>.
    /// </summary>
    public string? UserAgent { get; init; }
}

/// <summary>
/// A provider that never yields a checksum (disables verification).
/// </summary>
public sealed class NoChecksumProvider : IChecksumProvider
{
    /// <summary>
    /// Shared instance.
    /// </summary>
    public static NoChecksumProvider Instance { get; } = new();

    /// <inheritdoc />
    public Task<string?> GetExpectedSha256Async(GitHub.Models.GitHubRelease release, GitHub.Models.GitHubAsset asset, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}