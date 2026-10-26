using System.ComponentModel;
using System.Diagnostics;

namespace GitHubReleaseUpdater.AutoUpdate;

/// <summary>
/// The part of <see cref="UpdateController"/> that prepares for the installer and watches it.
/// </summary>
public sealed partial class UpdateController
{
    /// <summary>
    /// Lets the application get ready for being ended by the installer. A failure or a timeout is reported and ignored.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the application is ready or the time is up.</returns>
    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_options.BeforeInstallAsync is not { } prepare)
        {
            return;
        }

        try
        {
            await prepare(cancellationToken).WaitAsync(_options.BeforeInstallTimeout, _time, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            ReportError(ex);
        }
    }

    /// <summary>
    /// Waits for the installer to end, then for the grace period. An installer that cannot be watched, or that has no
    /// process to watch, is given ten times the grace period instead.
    /// </summary>
    /// <param name="processId">The process of the installer, or null when none was started.</param>
    /// <param name="launchedAt">When the installer was started (UTC), to tell its process from an unrelated one that got its id.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the installer is taken to have ended.</returns>
    private async Task WaitForInstallerAsync(int? processId, DateTime launchedAt, CancellationToken cancellationToken)
    {
        var wait = _options.InstallerGracePeriod;
        if (processId is null)
        {
            wait *= 10;
        }
        else
        {
            try
            {
                await (_options.WaitForProcessExitAsync ?? ((id, token) => WatchProcessAsync(id, launchedAt, token)))(processId.Value, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // An elevated installer, for example, may not be watched from here.
                wait *= 10;
                ReportError(ex);
            }
        }

        await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Tells whether a process with the installer's id can be the installer: it began no earlier than the installer was
    /// started, give or take a little for clocks that differ in their last digits.
    /// </summary>
    /// <param name="startedAt">When the process began, in UTC.</param>
    /// <param name="launchedAt">When the installer was started, in UTC.</param>
    /// <returns>False if the process is older than the launch, so it only reuses the id.</returns>
    internal static bool IsInstallerProcess(DateTime startedAt, DateTime launchedAt) =>
        startedAt >= launchedAt.AddSeconds(-2);

    /// <summary>
    /// Waits for a process to end.
    /// </summary>
    /// <param name="processId">The process.</param>
    /// <param name="launchedAt">When the installer was started (UTC); a process that began before that is not it.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes when the process ended.</returns>
    private static async Task WatchProcessAsync(int processId, DateTime launchedAt, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // The installer is already gone.
            return;
        }

        using (process)
        {
            try
            {
                if (!IsInstallerProcess(process.StartTime.ToUniversalTime(), launchedAt))
                {
                    // The installer ended and an unrelated process took its id.
                    return;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // The start time of an elevated process cannot be read; it is taken to be the installer.
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
