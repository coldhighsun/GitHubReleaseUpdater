namespace GitHubReleaseUpdater.AutoUpdate;

/// <summary>
/// The part of <see cref="UpdateController"/> that deletes what earlier downloads left.
/// </summary>
public sealed partial class UpdateController
{
    /// <summary>
    /// How many times <see cref="SweepLeftoversAsync"/> swept.
    /// </summary>
    private int _sweeps;

    /// <summary>
    /// Deletes what earlier downloads left in the download directory, off the calling thread. A file that cannot be
    /// deleted stays. The disk work is one hop to the thread pool.
    /// </summary>
    /// <param name="keepName">
    /// The file name of the package about to be downloaded, whose files are kept so that its partial file lets an
    /// interrupted download go on; null to delete everything, as long as <paramref name="stillWanted"/> allows it.
    /// </param>
    /// <param name="stillWanted">Asked before each file; false stops the cleanup, for when the situation changed meanwhile.</param>
    /// <returns>A task that completes when the files that could be deleted are deleted.</returns>
    private async Task DeleteOldDownloadsAsync(string? keepName, Func<bool> stillWanted)
    {
        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(_downloadDirectory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(_downloadDirectory))
                {
                    if (!stillWanted())
                    {
                        return;
                    }

                    if (keepName is null || !Path.GetFileName(file).StartsWith(keepName, StringComparison.Ordinal))
                    {
                        TryDelete(file);
                    }
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A chore on the side: whatever goes wrong with the directory must not fail the check or the download.
            ReportError(ex);
        }
    }

    /// <summary>
    /// Deletes a file of an earlier download. A file that cannot be deleted, typically because it is still in use, stays.
    /// </summary>
    /// <param name="file">The file.</param>
    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again next time.
        }
    }

    /// <summary>
    /// Deletes what earlier downloads left once the application is up to date, if <see cref="UpdateControllerOptions.CleanDownloadDirectory"/>
    /// is on. A round of <see cref="RunAsync"/> does this whether or not it made a check: the setup program of an update
    /// that was just installed is often still running at the first check after the restart, so its file cannot be
    /// deleted yet and has to be tried again later, also when the checks are throttled or switched off.
    /// </summary>
    /// <returns>A task that completes when the files that could be deleted are deleted.</returns>
    private Task SweepLeftoversAsync()
    {
        if (!_options.CleanDownloadDirectory || State != UpdateState.UpToDate || _installing)
        {
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _sweeps);
        return DeleteOldDownloadsAsync(null, () => _leftoversWanted);
    }

    /// <summary>
    /// Gets how many times a round of <see cref="RunAsync"/> swept on its own, which it does not when the check of the
    /// round already cleaned.
    /// </summary>
    internal int Sweeps => Volatile.Read(ref _sweeps);
}
