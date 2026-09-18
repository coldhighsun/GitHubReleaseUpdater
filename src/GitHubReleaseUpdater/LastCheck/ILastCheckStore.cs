using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater.LastCheck;

/// <summary>
/// Optional persistence hook for <see cref="ReleaseUpdater"/>: lets a caller opt into throttling checks to
/// <c>UpdaterOptions.MinimumCheckInterval</c> and remembering a version the user chose to skip, without the
/// library deciding where or how that state is stored. When <c>UpdaterOptions.LastCheckStore</c> is left
/// null (the default), <see cref="ReleaseUpdater.CheckForUpdateAsync"/> performs no throttling or skip
/// filtering and every call hits the API.
/// </summary>
public interface ILastCheckStore
{
    /// <summary>
    /// Returns the UTC time the last check completed, or null if none is recorded.
    /// </summary>
    Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the version the caller previously chose to skip, or null if none is recorded.
    /// </summary>
    Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a check completed at <paramref name="checkedAt"/>.
    /// </summary>
    Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a version to suppress from future <see cref="UpdateCheckResult.IsUpdateAvailable"/> results.
    /// The version still appears in <see cref="UpdateCheckResult.LatestVersion"/>. Use
    /// <see cref="ClearSkippedVersionAsync"/> to undo this.
    /// </summary>
    Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears any previously recorded skipped version.
    /// </summary>
    Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A process-lifetime <see cref="ILastCheckStore"/> backed by plain fields. Useful for tests or short-lived
/// tools; applications that need the state to survive a restart should implement <see cref="ILastCheckStore"/>
/// over their own settings storage instead.
/// </summary>
public sealed class InMemoryLastCheckStore : ILastCheckStore
{
    private DateTimeOffset? _lastCheckedAt;
    private SemanticVersion? _skippedVersion;

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_lastCheckedAt);

    /// <inheritdoc />
    public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_skippedVersion);

    /// <inheritdoc />
    public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default)
    {
        _lastCheckedAt = checkedAt;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        _skippedVersion = version;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default)
    {
        _skippedVersion = null;
        return Task.CompletedTask;
    }
}