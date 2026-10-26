using System.ComponentModel;
using System.Diagnostics;
using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Installation;
using GitHubReleaseUpdater.LastCheck;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater.AutoUpdate;

/// <summary>
/// The update flow of an application, without any user interface: it checks for a newer release now and then
/// (<see cref="RunAsync"/>) or on request (<see cref="CheckAsync"/>), remembers what it found, and installs it when
/// the user agrees (<see cref="InstallAsync"/>). What the user sees and what the application does is up to the
/// <see cref="IUpdatePresenter"/>.
/// </summary>
/// <remarks>
/// <para>
/// An automatic check announces a version it finds, once per version. A manual check bypasses throttling and a skipped
/// version, reports "up to date" or a failure, and goes straight to the confirmation when it finds a version. A manual
/// check requested while an automatic one is running is made as soon as that one ends: the call returns at once, several
/// such requests are made as one, and that check runs under the token of the call that was running, so cancelling that
/// call (for example when the application closes) drops it. Because it runs inside that call, the token the manual
/// caller passed cannot cancel it (nor its download), the presenter is called on the context of the running call, and
/// <see cref="RunAsync"/> does not go on to its next round until the check and whatever the user is asked after it (a
/// confirmation left open, a download) are done. An installation that is already running makes further calls do
/// nothing.
/// </para>
/// <para>
/// Whatever the presenter, a handler of <see cref="Changed"/> or <see cref="UpdateControllerOptions.IsAutoCheckEnabled"/>
/// throws is reported to <see cref="UpdateControllerOptions.OnError"/> and otherwise ignored, so the controller never
/// ends up in a state it cannot leave.
/// </para>
/// <para>
/// The controller is not thread-safe: call it from one thread, normally the UI thread. <see cref="Changed"/> and the
/// presenter are called on the context the call was made on.
/// </para>
/// </remarks>
public sealed class UpdateController
{
    /// <summary>
    /// Checks for, downloads and launches updates.
    /// </summary>
    private readonly ReleaseUpdater _updater;

    /// <summary>
    /// Talks to the user.
    /// </summary>
    private readonly IUpdatePresenter _presenter;

    /// <summary>
    /// The settings of this controller.
    /// </summary>
    private readonly UpdateControllerOptions _options;

    /// <summary>
    /// The clock the waits between automatic checks are measured on.
    /// </summary>
    private readonly TimeProvider _time;

    /// <summary>
    /// The directory the installer is downloaded to.
    /// </summary>
    private readonly string _downloadDirectory;

    /// <summary>
    /// How the installer is started, with <see cref="InstallerLaunchOptions.RequireVerified"/> applied if the options ask
    /// for it.
    /// </summary>
    private readonly InstallerLaunchOptions? _launchOptions;

    /// <summary>
    /// The result of the last check that found a release, which is what gets installed.
    /// </summary>
    private UpdateCheckResult? _lastCheck;

    /// <summary>
    /// The installer downloaded and verified for <see cref="Available"/>, or null.
    /// </summary>
    private DownloadResult? _download;

    /// <summary>
    /// The version the user was last told about by an automatic check.
    /// </summary>
    private SemanticVersion? _announced;

    /// <summary>
    /// Whether a download or an installation is asking the user or running; it stays set while the user is being asked, when
    /// <see cref="State"/> does not show it yet.
    /// </summary>
    private bool _installing;

    /// <summary>
    /// Whether the up-to-date state, in which leftovers of earlier downloads are deleted, still holds. Kept apart from
    /// <see cref="State"/> because the cleanup reads it on a thread-pool thread.
    /// </summary>
    private volatile bool _leftoversWanted;

    /// <summary>
    /// Whether the check that is running was asked for by the user.
    /// </summary>
    private bool _runningManual;

    /// <summary>
    /// Whether the user asked for a check while another was running, which is made when that one ends.
    /// </summary>
    private bool _manualQueued;

    /// <summary>
    /// The longest wait <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> accepts (a little under 49.7
    /// days); a longer <see cref="UpdateControllerOptions.InitialDelay"/> or <see cref="UpdateControllerOptions.CheckInterval"/>
    /// would make <see cref="RunAsync"/> fail long after the controller was created.
    /// </summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Creates a controller.
    /// </summary>
    /// <param name="updater">The updater that checks for, downloads and launches updates.</param>
    /// <param name="presenter">Talks to the user.</param>
    /// <param name="options">Settings; null uses the defaults.</param>
    public UpdateController(ReleaseUpdater updater, IUpdatePresenter presenter, UpdateControllerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(presenter);
        _updater = updater;
        _presenter = presenter;
        _options = options ?? new UpdateControllerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.InitialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.CheckInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.InitialDelay, MaxDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.CheckInterval, MaxDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.InstallerGracePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.InstallerGracePeriod, MaxDelay / 10);
        if (_options.BeforeInstallTimeout != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(_options.BeforeInstallTimeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.BeforeInstallTimeout, MaxDelay);
        }

        _time = _options.TimeProvider ?? TimeProvider.System;
        _downloadDirectory = _options.DownloadDirectory
            ?? Path.Combine(Path.GetTempPath(), "GitHubReleaseUpdater", $"{updater.Options.Owner}-{updater.Options.Repo}");
        var launch = _options.InstallerLaunchOptions;
        _launchOptions = _options.RequireVerifiedDownload && launch?.RequireVerified != true
            ? new InstallerLaunchOptions { Arguments = launch?.Arguments, WorkingDirectory = launch?.WorkingDirectory, RequireVerified = true }
            : launch;
    }

    /// <summary>
    /// Raised when <see cref="State"/> or <see cref="Available"/> changed.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets what the controller is doing or found last.
    /// </summary>
    public UpdateState State { get; private set; }

    /// <summary>
    /// Gets the newer version waiting to be installed, or null if none is known.
    /// </summary>
    public AvailableUpdate? Available { get; private set; }

    /// <summary>
    /// Gets the full path of the installer once <see cref="State"/> is <see cref="UpdateState.Downloaded"/>, or null.
    /// </summary>
    public string? DownloadedFile => _download?.FilePath;

    /// <summary>
    /// Gets the state to rest in when nothing is running: the installer is downloaded, or only available.
    /// </summary>
    private UpdateState Resting => _download is null ? UpdateState.Available : UpdateState.Downloaded;

    /// <summary>
    /// Checks for updates by itself: once after <see cref="UpdateControllerOptions.InitialDelay"/>, then every
    /// <see cref="UpdateControllerOptions.CheckInterval"/>, skipping the rounds in which
    /// <see cref="UpdateControllerOptions.IsAutoCheckEnabled"/> says no. Runs until cancelled.
    /// </summary>
    /// <param name="cancellationToken">Stops the loop, for example when the application closes.</param>
    /// <returns>A task that completes when cancelled; it does not throw because of the cancellation.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var delay = _options.InitialDelay;
            while (true)
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(true);
                delay = _options.CheckInterval;
                if (IsAutoCheckEnabled())
                {
                    await CheckAsync(manual: false, cancellationToken).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped by the caller.
        }
    }

    /// <summary>
    /// Looks for a newer version. Does nothing while an installation is running. A manual check requested while an
    /// automatic one is running is made when that one ends, and the call returns at once: the outcome reaches the user
    /// through the presenter only, requests made meanwhile are made as one check, and that check runs under the token of
    /// the call that was running (so cancelling it, for example when the application closes, drops the queued check)
    /// rather than the tokens of the callers that asked. That also means the queued check, the questions it leads to and
    /// its download cannot be cancelled by the caller that asked, are presented on the context of the running call, and
    /// hold up <see cref="RunAsync"/> until they are done.
    /// </summary>
    /// <param name="manual">True if the user asked for the check: throttling and a skipped version are bypassed, the
    /// outcome is reported to the presenter and a found version is offered for installation right away.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the check, and whatever the user is asked after it, is done.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (State == UpdateState.Checking)
        {
            if (manual && !_runningManual)
            {
                _manualQueued = true;
            }

            return;
        }

        if (State == UpdateState.Installing || _installing)
        {
            return;
        }

        _manualQueued = false;
        try
        {
            await CheckCoreAsync(manual, cancellationToken).ConfigureAwait(true);
            if (_manualQueued)
            {
                _manualQueued = false;
                await CheckCoreAsync(manual: true, cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            // A request queued behind a check that was cancelled or failed is dropped with it.
            _manualQueued = false;
        }
    }

    /// <summary>
    /// Remembers that the user does not want the version that was found, so that no check but a manual one offers it
    /// again, and forgets it here: <see cref="State"/> becomes <see cref="UpdateState.UpToDate"/>, as it does after any
    /// check that finds only a skipped version. Does nothing if no version was found, a check or an installation is running, or the
    /// updater has no <see cref="UpdaterOptions.LastCheckStore"/> to remember it in.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write to the store.</param>
    /// <returns>True if the version was recorded as skipped; false if there was nothing to skip or it could not be
    /// recorded (a failing store is also reported to <see cref="UpdateControllerOptions.OnError"/>).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<bool> SkipVersionAsync(CancellationToken cancellationToken = default)
    {
        var update = Available;
        var store = _updater.Options.LastCheckStore;
        if (update is null || store is null || _installing || State is UpdateState.Checking or UpdateState.Installing or UpdateState.Downloading)
        {
            return false;
        }

        try
        {
            await store.SetSkippedVersionAsync(update.Version, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError(ex);
            return false;
        }

        // A check may have started or found something else while the store was written; only forget what was skipped.
        if (ReferenceEquals(Available, update) && State is UpdateState.Available or UpdateState.Downloaded)
        {
            // One small delete, done before the state changes so that no download can start in between.
            if (DownloadedFile is { } file)
            {
                TryDelete(file);
            }

            _lastCheck = null;
            _download = null;
            SetState(UpdateState.UpToDate, null);
            await SetPendingAsync(false).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>
    /// Downloads and verifies the installer of the version that was found, without starting it, so that
    /// <see cref="InstallAsync"/> can start it later without downloading again. Does nothing if no version was found, it
    /// is already downloaded, or a check, download or installation is running. A failure is reported to
    /// <see cref="IUpdatePresenter.ReportInstallFailed"/> and leaves the state at <see cref="UpdateState.Available"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>A task that completes when the download ended.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        var check = _lastCheck;
        if (_installing || State != UpdateState.Available || check?.Update is not { } update)
        {
            return;
        }

        _installing = true;
        try
        {
            SetState(UpdateState.Downloading, update);
            Present(() => _presenter.ReportDownloading(update));
            try
            {
                _download = await DownloadCoreAsync(check, update, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(Resting, update);
                throw;
            }
            catch (Exception ex)
            {
                FailInstallation(update, ex);
                return;
            }

            SetState(UpdateState.Downloaded, update);
        }
        finally
        {
            _installing = false;
        }
    }

    /// <summary>
    /// Asks whether to install the version that was found and, if the user agrees, downloads it (unless
    /// <see cref="DownloadAsync"/> already did) and starts its installer. Does nothing if no version was found or a
    /// download or installation is already running. With <see cref="UpdateControllerOptions.WatchInstaller"/> the task
    /// completes only when the installer has ended.
    /// </summary>
    /// <param name="cancellationToken">Cancels the download and the wait for the installer.</param>
    /// <returns>A task that completes when the installer has been started, or the attempt was declined or failed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        var check = _lastCheck;
        if (_installing || State is UpdateState.Checking or UpdateState.Downloading || check?.Update is not { } update)
        {
            return;
        }

        _installing = true;
        try
        {
            if (!await ConfirmAsync(update).ConfigureAwait(true))
            {
                return;
            }

            SetState(UpdateState.Installing, update);
            try
            {
                // From here on the file is kept for another try whatever goes wrong, unless FailInstallation finds that it
                // is not what was verified.
                if (_download is not { } download || !File.Exists(download.FilePath))
                {
                    Present(() => _presenter.ReportDownloading(update));
                    download = await DownloadCoreAsync(check, update, cancellationToken).ConfigureAwait(true);
                    _download = download;
                }

                await PrepareAsync(cancellationToken).ConfigureAwait(true);
                var launchedAt = DateTime.UtcNow;
                var launched = await _updater.LaunchInstallerAsync(download, _launchOptions, cancellationToken).ConfigureAwait(true);
                if (!_options.WatchInstaller)
                {
                    // The installer is on its way to replace the application; nothing is left to offer.
                    _download = null;
                    SetState(UpdateState.Available, update);
                    Present(() => _presenter.InstallerStarted(update));
                    return;
                }

                Present(() => _presenter.InstallerStarted(update));
                await WaitForInstallerAsync(launched.ProcessId, launchedAt, cancellationToken).ConfigureAwait(true);
                SetState(UpdateState.Downloaded, update);
                Present(() => _presenter.InstallerEnded(update));
            }
            catch (InstallerLaunchException ex) when (ex.IsUserCancelled)
            {
                SetState(Resting, update);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(Resting, update);
                throw;
            }
            catch (Exception ex)
            {
                FailInstallation(update, ex);
            }
        }
        finally
        {
            _installing = false;
        }
    }

    /// <summary>
    /// Goes back to the resting state after a download or installation failed and tells the presenter. A downloaded file
    /// that is not what was verified is dropped, since starting it again would fail the same way.
    /// </summary>
    /// <param name="update">The update that was being downloaded or installed.</param>
    /// <param name="error">Why it failed.</param>
    private void FailInstallation(AvailableUpdate update, Exception error)
    {
        if (error is UpdaterException and not InstallerLaunchException)
        {
            _download = null;
        }

        SetState(Resting, update);
        Present(() => _presenter.ReportInstallFailed(update, error));
    }

    /// <summary>
    /// Downloads the installer of an update, after clearing what earlier downloads left if that is enabled.
    /// </summary>
    /// <param name="check">The check that found the update.</param>
    /// <param name="update">The update.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The verified download.</returns>
    private async Task<DownloadResult> DownloadCoreAsync(UpdateCheckResult check, AvailableUpdate update, CancellationToken cancellationToken)
    {
        _download = null;
        if (_options.CleanDownloadDirectory)
        {
            await DeleteOldDownloadsAsync(update.SelectedAsset!.Name, () => true).ConfigureAwait(true);
        }

        var progress = new Progress<DownloadProgress>(value => Present(() => _presenter.ReportProgress(update, value)));
        return await _updater.DownloadAsync(check, _downloadDirectory, progress, cancellationToken).ConfigureAwait(true);
    }

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
    /// Records in the store, if it can, whether a newer release is waiting.
    /// </summary>
    /// <param name="pending">True if a newer release was found.</param>
    /// <returns>A task that completes when it is recorded or failed.</returns>
    private async Task SetPendingAsync(bool pending)
    {
        if (_updater.Options.LastCheckStore is not IPendingUpdateStore store)
        {
            return;
        }

        try
        {
            await store.SetUpdatePendingAsync(pending).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError(ex);
        }
    }

    /// <summary>
    /// Makes one check and handles its outcome.
    /// </summary>
    /// <param name="manual">True if the user asked for the check.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the check, and whatever the user is asked after it, is done.</returns>
    private async Task CheckCoreAsync(bool manual, CancellationToken cancellationToken)
    {
        var before = State;
        _runningManual = manual;
        SetState(UpdateState.Checking, Available);
        UpdateCheckResult check;
        try
        {
            check = await _updater.CheckForUpdateAsync(bypassSkippedVersion: manual, bypassThrottle: manual, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Only the caller's own cancellation can come out of CheckForUpdateAsync; either way the state must not stay
            // at Checking.
            SetState(before, Available);
            throw;
        }
        finally
        {
            _runningManual = false;
        }

        if (check.LastCheckStoreError is { } recordError)
        {
            ReportError(recordError);
        }

        if (!check.Success)
        {
            Fail(check.Error, manual);
        }
        else if (check.Throttled)
        {
            // Nothing was asked of GitHub, so what was known before is still all there is.
            SetState(before, Available);
        }
        else if (check.Update is not { } update)
        {
            _lastCheck = null;
            _download = null;
            SetState(UpdateState.UpToDate, null);
            await SetPendingAsync(false).ConfigureAwait(true);
            if (_options.CleanDownloadDirectory)
            {
                // Looked at again before each file, so that a download that starts meanwhile keeps its files.
                await DeleteOldDownloadsAsync(null, () => _leftoversWanted).ConfigureAwait(true);
            }

            if (manual)
            {
                Present(_presenter.ReportUpToDate);
            }
        }
        else if (update.SelectedAsset is null)
        {
            Fail(new AssetNotFoundException($"Release {update.Version} has no asset the selector accepts.", update.Release.Assets.Select(a => a.Name).ToArray()), manual);
        }
        else
        {
            // An installer that was downloaded for this very version stays downloaded.
            if (_download is not { } kept || !File.Exists(kept.FilePath) || !kept.Asset.IsSameUpload(update.SelectedAsset))
            {
                _download = null;
            }

            _lastCheck = check;
            SetState(Resting, update);
            await SetPendingAsync(true).ConfigureAwait(true);
            if (manual)
            {
                // The user is asked right away, so a later automatic check need not announce this version again.
                _announced = update.Version;
                await InstallAsync(cancellationToken).ConfigureAwait(true);
            }
            else if (_announced != update.Version)
            {
                _announced = update.Version;
                Present(() => _presenter.Announce(update));
            }
        }
    }

    /// <summary>
    /// Records that a check failed, keeping a version found earlier.
    /// </summary>
    /// <param name="error">Why, or null if there was no exception.</param>
    /// <param name="manual">True if the user asked for the check and is told.</param>
    private void Fail(Exception? error, bool manual)
    {
        SetState(Available is null ? UpdateState.Failed : Resting, Available);
        if (manual)
        {
            Present(() => _presenter.ReportCheckFailed(error));
        }
    }

    /// <summary>
    /// Asks the presenter whether to install. A presenter that throws counts as a no.
    /// </summary>
    /// <param name="update">The release to install.</param>
    /// <returns>True if the user agrees.</returns>
    private async Task<bool> ConfirmAsync(AvailableUpdate update)
    {
        try
        {
            return await _presenter.ConfirmInstallAsync(update, _updater.Options.CurrentVersion).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return false;
        }
    }

    /// <summary>
    /// Asks <see cref="UpdateControllerOptions.IsAutoCheckEnabled"/>. A delegate that throws counts as a no for this round.
    /// </summary>
    /// <returns>True if the automatic check is to be made.</returns>
    private bool IsAutoCheckEnabled()
    {
        try
        {
            return _options.IsAutoCheckEnabled?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return false;
        }
    }

    /// <summary>
    /// Calls the presenter without letting an exception of its out, which would leave the controller half-way through a
    /// change of state.
    /// </summary>
    /// <param name="call">The call to the presenter.</param>
    private void Present(Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    /// <summary>
    /// Passes an exception that has nowhere else to go to <see cref="UpdateControllerOptions.OnError"/>.
    /// </summary>
    /// <param name="error">What went wrong.</param>
    private void ReportError(Exception error)
    {
        try
        {
            _options.OnError?.Invoke(error);
        }
        catch (Exception)
        {
            // The error reporter itself failed; there is nobody left to tell.
        }
    }

    /// <summary>
    /// Changes the state and tells the listeners.
    /// </summary>
    /// <param name="state">The new state.</param>
    /// <param name="available">The newer version waiting to be installed, if any.</param>
    private void SetState(UpdateState state, AvailableUpdate? available)
    {
        State = state;
        _leftoversWanted = state == UpdateState.UpToDate;
        Available = available;
        Present(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
