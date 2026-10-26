using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Installation;

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
/// check requested while an automatic one is running is made as soon as that one ends. An installation that is already
/// running makes further calls do nothing.
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
    /// The version the user was last told about by an automatic check.
    /// </summary>
    private Versioning.SemanticVersion? _announced;

    /// <summary>
    /// Whether an installation is asking the user or running; it stays set while the user is being asked, when
    /// <see cref="State"/> does not show it yet.
    /// </summary>
    private bool _installing;

    /// <summary>
    /// Whether the check that is running was asked for by the user.
    /// </summary>
    private bool _runningManual;

    /// <summary>
    /// Whether the user asked for a check while an automatic one was running, which is made when that one ends.
    /// </summary>
    private bool _manualQueued;

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
    /// automatic one is running is made when that one ends, and the call returns at once.
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
        await CheckCoreAsync(manual, cancellationToken).ConfigureAwait(true);
        if (_manualQueued)
        {
            _manualQueued = false;
            await CheckCoreAsync(manual: true, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Asks whether to install the version that was found and, if the user agrees, downloads it and starts its
    /// installer. Does nothing if no version was found or an installation is already running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>A task that completes when the installer has been started, or the attempt was declined or failed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        var check = _lastCheck;
        if (_installing || State == UpdateState.Checking || check?.Update is not { } update)
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
            Present(() => _presenter.ReportDownloading(update));
            var progress = new Progress<DownloadProgress>(value => Present(() => _presenter.ReportProgress(update, value)));
            try
            {
                await _updater.DownloadAndLaunchInstallerAsync(check, _downloadDirectory, _launchOptions, progress, cancellationToken).ConfigureAwait(true);
            }
            catch (InstallerLaunchException ex) when (ex.IsUserCancelled)
            {
                SetState(UpdateState.Available, update);
                return;
            }
            catch (OperationCanceledException)
            {
                SetState(UpdateState.Available, update);
                throw;
            }
            catch (Exception ex)
            {
                SetState(UpdateState.Available, update);
                Present(() => _presenter.ReportInstallFailed(update, ex));
                return;
            }

            SetState(UpdateState.Available, update);
            Present(() => _presenter.InstallerStarted(update));
        }
        finally
        {
            _installing = false;
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
            SetState(UpdateState.UpToDate, null);
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
            _lastCheck = check;
            SetState(UpdateState.Available, update);
            if (manual)
            {
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
        SetState(Available is null ? UpdateState.Failed : UpdateState.Available, Available);
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
        Available = available;
        Present(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
