using System.Diagnostics;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Download;

/// <summary>Streams a release asset to disk with progress reporting and cancellation.</summary>
public sealed class AssetDownloader
{
    private const string PartialSuffix = ".partial";

    private readonly IGitHubReleaseClient _client;

    /// <summary>Buffer size used when copying the response stream.</summary>
    public int BufferSize { get; init; } = 81920;

    /// <summary>Minimum interval between progress callbacks.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Creates a downloader.</summary>
    public AssetDownloader(IGitHubReleaseClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="directory"/> (created if missing) and returns the final file path.
    /// Data is written to <c>&lt;name&gt;.partial</c> and atomically renamed on completion; a failed or cancelled download leaves no partial file behind.
    /// </summary>
    /// <param name="asset">Asset to download.</param>
    /// <param name="directory">Destination directory.</param>
    /// <param name="fileName">Optional destination file name; defaults to the asset name.</param>
    /// <param name="overwrite">Whether to replace an existing file at the destination.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> DownloadAsync(
        GitHubAsset asset,
        string directory,
        string? fileName = null,
        bool overwrite = true,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var name = SanitizeFileName(fileName ?? asset.Name);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, name);
        var partialPath = finalPath + PartialSuffix;

        if (File.Exists(finalPath) && !overwrite)
            throw new IOException($"File already exists: {finalPath}");

        try
        {
            await using (var source = await _client.OpenAssetStreamAsync(asset, cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var total = source.ContentLength ?? (asset.Size > 0 ? asset.Size : (long?)null);
                await CopyWithProgressAsync(source.Stream, target, total, progress, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partialPath, finalPath);
            return finalPath;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private async Task CopyWithProgressAsync(Stream source, Stream target, long? total, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long received = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        progress?.Report(new DownloadProgress(0, total, TimeSpan.Zero));
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (progress is not null && stopwatch.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                progress.Report(new DownloadProgress(received, total, stopwatch.Elapsed));
            }
        }

        if (total is { } expected && received != expected)
            throw new UpdaterException($"Download truncated: expected {expected} bytes but received {received}.");

        progress?.Report(new DownloadProgress(received, total ?? received, stopwatch.Elapsed));
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (cleaned.Length == 0 || cleaned is "." or "..")
            throw new ArgumentException($"Invalid asset file name '{name}'.", nameof(name));
        return cleaned;
    }

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
