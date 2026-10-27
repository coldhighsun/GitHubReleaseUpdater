using System.ComponentModel;
using System.Diagnostics;

namespace GitHubReleaseUpdater.AutoUpdate;

/// <summary>
/// The part of <see cref="UpdateController"/> that prepares for the installer and watches it.
/// </summary>
public sealed partial class UpdateController
{
    /// <summary>
    /// Lets the application get ready for being ended by the installer. A timeout or a failure is reported and ignored,
    /// unless <see cref="UpdateControllerOptions.BeforeInstallMustSucceed"/> is set, which rethrows it.
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
            // Only a preparation that stops the installation is told to stop: the undo must not run beside it. Otherwise
            // it may finish in the background, say saving state, while the installer starts.
            await RunHookAsync(prepare, _options.BeforeInstallTimeout, cancellationToken, _options.BeforeInstallMustSucceed).ConfigureAwait(true);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            if (_options.BeforeInstallMustSucceed)
            {
                throw;
            }

            ReportError(ex);
        }
    }

    /// <summary>
    /// Lets the application undo its preparation after the installer did not start. A failure or a timeout is reported
    /// and ignored; the token of the callback is cancelled when <see cref="UpdateControllerOptions.InstallNotStartedTimeout"/>
    /// is up.
    /// </summary>
    /// <param name="installerNotStarted">Whether the application was prepared and the installer was not started.</param>
    /// <returns>A task that completes when the application has undone it or the time is up.</returns>
    private async Task UndoPrepareAsync(bool installerNotStarted)
    {
        if (!installerNotStarted || _options.InstallNotStartedAsync is not { } undo)
        {
            return;
        }

        try
        {
            await RunHookAsync(undo, _options.InstallNotStartedTimeout, CancellationToken.None, cancelOnFailure: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    /// <summary>
    /// Runs a hook of the application and stops waiting for it after the timeout.
    /// </summary>
    /// <param name="hook">The hook.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the hook and the wait.</param>
    /// <param name="cancelOnFailure">Whether the token given to the hook is cancelled when the wait ends in a failure or a timeout.</param>
    /// <returns>A task that completes when the hook has or the time is up.</returns>
    private async Task RunHookAsync(Func<CancellationToken, Task> hook, TimeSpan timeout, CancellationToken cancellationToken, bool cancelOnFailure)
    {
        if (!cancelOnFailure)
        {
            // The hook may outlive the wait, so it gets the caller's token itself, not one that is gone by then.
            await hook(cancellationToken).WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(true);
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await hook(stop.Token).WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            try
            {
                await stop.CancelAsync().ConfigureAwait(true);
            }
            catch (Exception cancelError)
            {
                // A handler registered on the token threw; that must not hide why the hook failed.
                ReportError(cancelError);
            }

            throw;
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
