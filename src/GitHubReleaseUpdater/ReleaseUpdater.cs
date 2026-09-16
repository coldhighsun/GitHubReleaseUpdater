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
public sealed class ReleaseUpdater : IDisposable
{
    /// <summary>
    /// The options this updater was created with.
    /// </summary>
    private readonly UpdaterOptions _options;

    /// <summary>
    /// The GitHub client used to query releases and download assets.
    /// </summary>
    private readonly IGitHubReleaseClient _client;

    /// <summary>
    /// True when this updater created <see cref="_client"/> and is responsible for disposing it.
    /// </summary>
    private readonly bool _ownsClient;

    /// <summary>
    /// Chooses which asset of a release to download.
    /// </summary>
    private readonly IAssetSelector _selector;

    /// <summary>
    /// Resolves the expected checksum for a downloaded asset.
    /// </summary>
    private readonly IChecksumProvider _checksums;

    /// <summary>
    /// Streams asset bytes to disk with progress reporting.
    /// </summary>
    private readonly AssetDownloader _downloader;

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
    /// The options this updater was created with.
    /// </summary>
    public UpdaterOptions Options => _options;

    /// <summary>
    /// The underlying GitHub client.
    /// </summary>
    public IGitHubReleaseClient Client => _client;

    /// <summary>
    /// Determines the newest applicable release and whether it is newer than <see cref="UpdaterOptions.CurrentVersion"/>.
    /// Never throws for "no update"; throws <see cref="GitHubApiException"/> on API failures.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
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

        GitHubAsset? asset = null;
        if (best is not null && bestVersion is not null && bestVersion > _options.CurrentVersion)
            asset = _selector.Select(best);

        return new UpdateCheckResult(_options.CurrentVersion, bestVersion, best, asset, skipped);
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
        if (!check.IsUpdateAvailable || check.Release is null)
            throw new InvalidOperationException("No update is available.");
        if (check.SelectedAsset is null)
            throw new AssetNotFoundException(
                $"No asset in release {check.Release.TagName} matched the configured selector.",
                check.Release.Assets.Select(a => a.Name).ToArray());
        return DownloadAsync(check.Release, check.SelectedAsset, directory, progress, cancellationToken);
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient && _client is IDisposable d) d.Dispose();
    }
}
