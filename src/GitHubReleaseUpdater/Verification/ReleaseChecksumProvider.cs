using System.Runtime.ExceptionServices;
using GitHubReleaseUpdater.Exceptions;
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
    /// <summary>
    /// Known exact file names of aggregate SHA-256 sums files.
    /// </summary>
    private static readonly string[] AggregateNames =
    [
        "sha256sums", "sha256sums.txt", "sha256sum.txt", "sha256sum", "checksums.txt", "checksums", "checksums.sha256", "sha256.txt",
    ];

    /// <summary>
    /// Largest checksum file read, in bytes (far above any real list of checksums); anything bigger is not loaded into memory.
    /// </summary>
    private const long MaxChecksumFileSize = 16 * 1024 * 1024;

    /// <summary>
    /// The GitHub client used to read sidecar and aggregate checksum assets.
    /// </summary>
    private readonly IGitHubReleaseClient _client;

    /// <summary>
    /// Creates a provider that reads sidecar/aggregate files through <paramref name="client"/>.
    /// </summary>
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
        var digest = ChecksumParser.NormalizeDigest(asset.Digest);
        var failures = new List<Exception>();
        var readFiles = 0;
        if (sidecar is not null && await TryReadAsync(sidecar, failures, cancellationToken).ConfigureAwait(false) is { } sidecarText)
        {
            readFiles++;
            var found = ChecksumParser.FindFor(ChecksumParser.ParseSums(sidecarText), asset.Name);
            if (found is not null) return found;
        }

        // 2. aggregate sums file(s)
        foreach (var candidate in release.Assets.Where(a => IsAggregate(a.Name)))
        {
            if (await TryReadAsync(candidate, failures, cancellationToken).ConfigureAwait(false) is not { } text) continue;
            readFiles++;
            var sums = ChecksumParser.ParseSums(text);
            if (sums.TryGetValue(asset.Name, out var h)) return h;
        }

        // Every file was tried. Without a digest to fall back on, a checksum file that exists but could not be used must
        // fail the download rather than quietly leave it unverified, since it may be the one that lists the asset.
        if (digest is null && failures.Count > 0)
        {
            if (readFiles == 0)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            throw new UpdaterException(
                $"No checksum for asset '{asset.Name}' was found: {readFiles} checksum file(s) were read without listing it, and {failures.Count} could not be used ({failures[0].Message}).",
                failures[0]);
        }

        // 3. GitHub-reported digest
        return digest;
    }

    /// <summary>
    /// Reads a checksum file. A file that is too large to be one, or a failed request, is added to
    /// <paramref name="failures"/> and gives null, so that the other checksum files and in the end the digest GitHub
    /// reports are still tried. The caller's own cancellation is always thrown.
    /// </summary>
    private async Task<string?> TryReadAsync(GitHubAsset file, List<Exception> failures, CancellationToken cancellationToken)
    {
        if (file.Size > MaxChecksumFileSize)
        {
            failures.Add(new UpdaterException($"The checksum file '{file.Name}' is larger than {MaxChecksumFileSize} bytes and is not read."));
            return null;
        }

        try
        {
            return await _client.ReadAssetTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or GitHubApiException or TimeoutException or IOException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            failures.Add(ex);
            return null;
        }
    }

    /// <summary>
    /// True when the name looks like an aggregate SHA-256 sums file.
    /// </summary>
    public static bool IsAggregate(string name)
    {
        var lower = name.ToLowerInvariant();
        if (AggregateNames.Contains(lower)) return true;
        // e.g. myapp_1.2.3_checksums.txt, myapp-SHA256SUMS.txt
        return (lower.EndsWith("checksums.txt", StringComparison.Ordinal) || lower.EndsWith("sha256sums.txt", StringComparison.Ordinal) || lower.EndsWith("sha256sums", StringComparison.Ordinal))
               && !lower.Contains("sha512", StringComparison.Ordinal);
    }
}
