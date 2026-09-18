using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Verification;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater;

/// <summary>
/// High-level entry point: check a GitHub repository for a newer release, download the matching asset and verify it.
/// </summary>
/// <remarks>
/// When <see cref="UpdaterOptions.HttpClient"/> is left null, every <see cref="ReleaseUpdater"/> created this
/// way shares one process-wide <see cref="HttpClient"/> internally, so constructing a new instance per check
/// does not create a new connection pool each time. If you instead supply your own
/// <see cref="UpdaterOptions.HttpClient"/> (e.g. from <c>IHttpClientFactory</c>), the usual guidance applies:
/// reuse that client rather than creating one per check.
/// </remarks>
public sealed class ReleaseUpdater : IDisposable
{
    /// <summary>
    /// Resolves the expected checksum for a downloaded asset.
    /// </summary>
    private readonly IChecksumProvider _checksums;

    /// <summary>
    /// The GitHub client used to query releases and download assets.
    /// </summary>
    private readonly IGitHubReleaseClient _client;

    /// <summary>
    /// Streams asset bytes to disk with progress reporting.
    /// </summary>
    private readonly AssetDownloader _downloader;

    /// <summary>
    /// The options this updater was created with.
    /// </summary>
    private readonly UpdaterOptions _options;

    /// <summary>
    /// True when this updater created <see cref="_client"/> and is responsible for disposing it.
    /// </summary>
    private readonly bool _ownsClient;

    /// <summary>
    /// Chooses which asset of a release to download.
    /// </summary>
    private readonly IAssetSelector _selector;

    /// <summary>
    /// Creates an updater that talks to GitHub with a <see cref="GitHubReleaseClient"/> built from <paramref name="options"/>.
    /// </summary>
    public ReleaseUpdater(UpdaterOptions options)
        : this(options, new GitHubReleaseClient(options?.BaseUrl, options?.Token, options?.UserAgent, options?.HttpClient), ownsClient: true)
    {
    }

    /// <summary>
    /// Creates an updater over a caller-supplied client (useful for testing or custom transports).
    /// </summary>
    public ReleaseUpdater(UpdaterOptions options, IGitHubReleaseClient client) : this(options, client, ownsClient: false) { }

    /// <summary>
    /// Validates <paramref name="options"/> and wires up the selector, checksum provider and downloader.
    /// </summary>
    private ReleaseUpdater(UpdaterOptions options, IGitHubReleaseClient client, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Repo);
        ArgumentNullException.ThrowIfNull(options.CurrentVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReleaseScanCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ReleaseScanCount, 100);

        _options = options;
        _client = client;
        _ownsClient = ownsClient;
        _selector = options.AssetSelector ?? new RuntimeAssetSelector();
        _checksums = options.ChecksumProvider ?? new ReleaseChecksumProvider(client);
        _downloader = new AssetDownloader(client);
    }

    /// <summary>
    /// The underlying GitHub client.
    /// </summary>
    public IGitHubReleaseClient Client => _client;

    /// <summary>
    /// The options this updater was created with.
    /// </summary>
    public UpdaterOptions Options => _options;

    /// <summary>
    /// Determines the newest applicable release and whether it is newer than <see cref="UpdaterOptions.CurrentVersion"/>.
    /// Never throws for "no update"; throws <see cref="GitHubApiException"/> on API failures.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var store = _options.LastCheckStore;
        if (store is not null && _options.MinimumCheckInterval is { } interval)
        {
            var lastChecked = await store.GetLastCheckedAtAsync(cancellationToken).ConfigureAwait(false);
            if (lastChecked is not null && DateTimeOffset.UtcNow - lastChecked.Value < interval)
                return UpdateCheckResult.ThrottledResult(_options.CurrentVersion);
        }

        var skipped = new List<string>();
        GitHubRelease? best = null;
        SemanticVersion? bestVersion = null;

        if (_options.IncludePrerelease)
        {
            var releases = await _client.ListReleasesAsync(_options.Owner, _options.Repo, _options.ReleaseScanCount, 1, cancellationToken).ConfigureAwait(false);
            foreach (var r in releases)
            {
                if (r.Draft) continue;
                if (!SemanticVersion.TryParse(r.TagName, out var v, _options.TagPrefix))
                {
                    skipped.Add(r.TagName);
                    continue;
                }
                if (bestVersion is null || v > bestVersion)
                {
                    best = r;
                    bestVersion = v;
                }
            }
        }
        else
        {
            var latest = await _client.GetLatestReleaseAsync(_options.Owner, _options.Repo, cancellationToken).ConfigureAwait(false);
            if (latest is not null)
            {
                if (SemanticVersion.TryParse(latest.TagName, out var v, _options.TagPrefix))
                {
                    best = latest;
                    bestVersion = v;
                }
                else
                {
                    skipped.Add(latest.TagName);
                }
            }
        }

        var isUpdateAvailable = best is not null && bestVersion is not null && bestVersion > _options.CurrentVersion;
        if (isUpdateAvailable && store is not null)
        {
            var skippedVersion = await store.GetSkippedVersionAsync(cancellationToken).ConfigureAwait(false);
            if (skippedVersion is not null && bestVersion == skippedVersion)
                isUpdateAvailable = false;
        }

        GitHubAsset? asset = null;
        if (isUpdateAvailable)
            asset = _selector.Select(best!);

        if (store is not null)
            await store.SetLastCheckedAtAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        return new UpdateCheckResult(_options.CurrentVersion, bestVersion, best, asset, skipped, isUpdateAvailable);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient && _client is IDisposable d) d.Dispose();
    }

    /// <summary>
    /// Downloads the asset selected in <paramref name="check"/> into <paramref name="directory"/>, then verifies its SHA-256
    /// when a checksum can be resolved. On mismatch the file is deleted and <see cref="ChecksumMismatchException"/> is thrown.
    /// </summary>
    /// <exception cref="InvalidOperationException">No update is available in <paramref name="check"/>.</exception>
    /// <exception cref="AssetNotFoundException">No asset matched the selector.</exception>
    public Task<DownloadResult> DownloadAsync(UpdateCheckResult check, string directory, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var update = check.Update ?? throw new InvalidOperationException("No update is available.");
        if (update.SelectedAsset is null)
            throw new AssetNotFoundException(
                $"No asset in release {update.Release.TagName} matched the configured selector.",
                update.Release.Assets.Select(a => a.Name).ToArray());
        return DownloadAsync(update.Release, update.SelectedAsset, directory, progress, cancellationToken);
    }

    /// <summary>
    /// Downloads and verifies an explicit asset of a release.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(GitHubRelease release, GitHubAsset asset, string directory, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // Resolve the expected checksum first so a missing one (with RequireChecksum) fails before any bytes are transferred.
        var expected = await _checksums.GetExpectedSha256Async(release, asset, cancellationToken).ConfigureAwait(false);
        if (expected is null && _options.RequireChecksum)
            throw new UpdaterException($"No checksum is available for asset '{asset.Name}' and RequireChecksum is enabled.");

        var path = await _downloader.DownloadAsync(asset, directory, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);

        string actual;
        try
        {
            actual = await FileHasher.Sha256Async(path, cancellationToken).ConfigureAwait(false);
            if (expected is not null && !FileHasher.HashEquals(expected, actual))
            {
                File.Delete(path);
                throw new ChecksumMismatchException(path, expected, actual);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            throw;
        }

        return new DownloadResult(path, asset, actual, verified: expected is not null);
    }

    /// <summary>
    /// Deletes a file if it exists, silently ignoring I/O and permission errors.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}