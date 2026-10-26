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
public sealed partial class UpdateController
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
    /// The latest progress, held in an object so that it is read and written as a whole by different threads.
    /// </summary>
    private ProgressBox? _progress;

    /// <summary>
    /// Counts the downloads, so that a late progress report of an earlier one is ignored.
    /// </summary>
    private int _downloadGeneration;

    /// <summary>
    /// How many times a check found the application up to date and cleaned the download directory, so that a round of
    /// <see cref="RunAsync"/> can tell whether it still has to.
    /// </summary>
    private int _cleanups;

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
    /// Raised each time a chunk of the package arrived, with <see cref="Progress"/> updated. It is raised for every chunk,
    /// so a handler that updates a display should only react to a change of the whole percent.
    /// </summary>
    public event EventHandler? ProgressChanged;

    /// <summary>
    /// Gets what the controller is doing or found last.
    /// </summary>
    public UpdateState State { get; private set; }

    /// <summary>
    /// Gets the newer version waiting to be installed, or null if none is known.
    /// </summary>
    public AvailableUpdate? Available { get; private set; }

    /// <summary>
    /// Gets why the last check, download or installation failed, or null. It is set together with the state change that
    /// follows the failure, so a <see cref="Changed"/> handler sees it, and cleared by the next change of state. A manual
    /// check that only finds a release without a package is not a failure and leaves it null.
    /// </summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// Gets the progress of the download that is running or ran last, or null before the first chunk of a download.
    /// </summary>
    public DownloadProgress? Progress => Volatile.Read(ref _progress)?.Value;

    /// <summary>
    /// Gets the full path of the downloaded package once <see cref="State"/> is <see cref="UpdateState.Downloaded"/>, or null.
    /// </summary>
    public string? DownloadedFile => _download?.FilePath;

    /// <summary>
    /// Gets a value indicating whether the newer version found has a package (installer or archive) that can be downloaded. False while no
    /// version is known and for a release without a package the selector accepts; <see cref="Available"/> still holds
    /// that version, so the application can show it and point to the release page.
    /// </summary>
    public bool HasInstaller => Available?.SelectedAsset is not null;

    /// <summary>
    /// Gets the state to rest in when nothing is running: the installer is downloaded, or only available.
    /// </summary>
    private UpdateState Resting => _download is null ? UpdateState.Available : UpdateState.Downloaded;

    /// <summary>
    /// Checks for updates by itself: once after <see cref="UpdateControllerOptions.InitialDelay"/>, then every
    /// <see cref="UpdateControllerOptions.CheckInterval"/>, skipping the rounds in which
    /// <see cref="UpdateControllerOptions.IsAutoCheckEnabled"/> says no (or, with
    /// <see cref="UpdateControllerOptions.SkipChecksWhileUpdateKnown"/>, a newer version is already known). When the
    /// updater has a <see cref="UpdaterOptions.MinimumCheckInterval"/> that is not longer than the check interval, only the
    /// first round is throttled by it, so that a check interval equal to the minimum interval is not skipped every
    /// other time. Runs until cancelled.
    /// </summary>
    /// <param name="cancellationToken">Stops the loop, for example when the application closes.</param>
    /// <returns>A task that completes when cancelled; it does not throw because of the cancellation.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var delay = _options.InitialDelay;
            var checkedOnce = false;
            while (true)
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(true);
                var cleansBefore = _cleanups;
                delay = _options.CheckInterval;
                if (IsAutoCheckEnabled() && !(_options.SkipChecksWhileUpdateKnown && Available is not null))
                {

                    // The updater would skip a round that comes exactly one minimum interval after the last check, because
                    // the last check was recorded when it ended, a little after the round began. The rounds are already
                    // spaced by CheckInterval, so when that is not shorter than the minimum interval only the first
                    // round, which follows a restart, is left to the updater's throttling.
                    var bypassThrottle = checkedOnce && _updater.Options.MinimumCheckInterval is { } minimum && _options.CheckInterval >= minimum;
                    checkedOnce |= await RunCheckAsync(manual: false, bypassThrottle, cancellationToken).ConfigureAwait(true);
                }

                if (_cleanups == cleansBefore)
                {
                    await SweepLeftoversAsync().ConfigureAwait(true);
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
    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default) =>
        await RunCheckAsync(manual, bypassThrottle: false, cancellationToken).ConfigureAwait(true);

    /// <summary>
    /// Makes a check as <see cref="CheckAsync"/> does, and tells whether one was made.
    /// </summary>
    /// <param name="manual">True if the user asked for the check.</param>
    /// <param name="bypassThrottle">True to ignore the minimum interval of the updater, as a manual check does.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>False if the call only queued a manual check or did nothing because a check or an installation was running.</returns>
    private async Task<bool> RunCheckAsync(bool manual, bool bypassThrottle, CancellationToken cancellationToken)
    {
        if (State == UpdateState.Checking)
        {
            if (manual && !_runningManual)
            {
                _manualQueued = true;
            }

            return false;
        }

        if (State == UpdateState.Installing || _installing)
        {
            return false;
        }

        _manualQueued = false;
        try
        {
            await CheckCoreAsync(manual, bypassThrottle, cancellationToken).ConfigureAwait(true);
            if (_manualQueued)
            {
                _manualQueued = false;
                await CheckCoreAsync(manual: true, bypassThrottle: false, cancellationToken).ConfigureAwait(true);
            }

            return true;
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

        if (update.SelectedAsset is null)
        {
            ReportNoPackage(update);
            return;
        }

        _installing = true;
        try
        {
            SetState(UpdateState.Downloading, update);
            Present(() => _presenter.ReportDownloading(update));
            DownloadResult download;
            try
            {
                download = await DownloadCoreAsync(check, update, cancellationToken).ConfigureAwait(true);
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

            _download = download;
            SetState(UpdateState.Downloaded, update);
            Present(() => _presenter.PackageDownloaded(update, download.FilePath));
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
    /// completes only when the installer has ended. With <see cref="UpdateControllerOptions.LaunchInstaller"/> turned off
    /// nothing is started: the package is only downloaded and verified, the state becomes <see cref="UpdateState.Downloaded"/>
    /// and <see cref="IUpdatePresenter.PackageDownloaded"/> is called.
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

        if (update.SelectedAsset is null)
        {
            ReportNoPackage(update);
            return;
        }

        if (!_options.LaunchInstaller && State == UpdateState.Downloaded && _download is { } have && File.Exists(have.FilePath))
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

                if (!_options.LaunchInstaller)
                {
                    SetState(UpdateState.Downloaded, update);
                    Present(() => _presenter.PackageDownloaded(update, download.FilePath));
                    return;
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
    /// Tells the presenter that a version cannot be downloaded or installed because its release has no package the
    /// selector accepts, so a request for it does not end in silence.
    /// </summary>
    /// <param name="update">The release without a package.</param>
    private void ReportNoPackage(AvailableUpdate update)
    {
        var error = new AssetNotFoundException($"Release {update.Version} has no asset the selector accepts.", update.Release.Assets.Select(a => a.Name).ToArray());
        Present(() => _presenter.ReportInstallFailed(update, error));
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

        SetState(Resting, update, error);
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

        var generation = Interlocked.Increment(ref _downloadGeneration);
        Volatile.Write(ref _progress, null);
        var progress = new Progress<DownloadProgress>(value => ApplyProgress(generation, update, value));
        var download = await _updater.DownloadAsync(check, _downloadDirectory, progress, cancellationToken).ConfigureAwait(true);
        if (!_options.LaunchInstaller && _options.RequireVerifiedDownload && !download.Verified)
        {
            // Nothing is launched, so LaunchInstallerAsync will not apply the requirement; the user would unpack the file.
            TryDelete(download.FilePath);
            throw new UpdaterException($"The download of '{download.Asset.Name}' was not checksum-verified and RequireVerifiedDownload is enabled, so it is not offered.");
        }

        return download;
    }

    /// <summary>
    /// Takes a progress report of a download. Reports are delivered asynchronously, so one of an earlier download may
    /// arrive after the next has started; it is ignored.
    /// </summary>
    /// <param name="generation">The number of the download the report belongs to.</param>
    /// <param name="update">The release being downloaded.</param>
    /// <param name="value">The progress.</param>
    internal void ApplyProgress(int generation, AvailableUpdate update, DownloadProgress value)
    {
        if (Volatile.Read(ref _downloadGeneration) != generation)
        {
            return;
        }

        Volatile.Write(ref _progress, new ProgressBox(value));

        // Called for every chunk, so without closures: a handler or presenter that throws goes to OnError like elsewhere.
        try
        {
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }

        try
        {
            _presenter.ReportProgress(update, value);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    /// <summary>
    /// Gets the number of the download that is running or ran last.
    /// </summary>
    internal int DownloadGeneration => Volatile.Read(ref _downloadGeneration);

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
    /// <param name="bypassThrottle">True to ignore the minimum interval of the updater although the check is not manual.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the check, and whatever the user is asked after it, is done.</returns>
    private async Task CheckCoreAsync(bool manual, bool bypassThrottle, CancellationToken cancellationToken)
    {
        var before = State;
        var errorBefore = LastError;
        _runningManual = manual;
        SetState(UpdateState.Checking, Available);
        UpdateCheckResult check;
        try
        {
            check = await _updater.CheckForUpdateAsync(bypassSkippedVersion: manual, bypassThrottle: manual || bypassThrottle, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Only the caller's own cancellation can come out of CheckForUpdateAsync; either way the state must not stay
            // at Checking.
            SetState(before, Available, errorBefore);
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
            SetState(before, Available, errorBefore);
        }
        else if (check.Update is not { } update)
        {
            _lastCheck = null;
            _download = null;
            SetState(UpdateState.UpToDate, null);
            await SetPendingAsync(false).ConfigureAwait(true);
            if (_options.CleanDownloadDirectory)
            {
                _cleanups++;

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
            // The version exists although nothing can be installed from it: it is kept, so that the application can show
            // it and link to the release, but nothing is announced (so the check need not be repeated after a restart) and
            // the user is told only when they asked.
            _download = null;
            _lastCheck = check;
            SetState(UpdateState.Available, update);
            await SetPendingAsync(false).ConfigureAwait(true);
            if (manual)
            {
                Present(() => _presenter.ReportCheckFailed(new AssetNotFoundException($"Release {update.Version} has no asset the selector accepts.", update.Release.Assets.Select(a => a.Name).ToArray())));
            }
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
                // The user sees the result of their check, so a later automatic check need not announce this version again.
                _announced = update.Version;
                if (_options.AskToInstallAfterManualCheck)
                {
                    await InstallAsync(cancellationToken).ConfigureAwait(true);
                }
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
        SetState(Available is null ? UpdateState.Failed : Resting, Available, error);
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
    /// <param name="error">Why this state follows a failure, or null; a state that differs from the last one clears an earlier error.</param>
    private void SetState(UpdateState state, AvailableUpdate? available, Exception? error = null)
    {
        if (error is not null)
        {
            LastError = error;
        }
        else if (state != State)
        {
            LastError = null;
        }

        State = state;
        _leftoversWanted = state == UpdateState.UpToDate;
        Available = available;
        Present(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// A progress value kept as one object.
    /// </summary>
    /// <param name="Value">The progress.</param>
    private sealed record ProgressBox(DownloadProgress Value);
}
