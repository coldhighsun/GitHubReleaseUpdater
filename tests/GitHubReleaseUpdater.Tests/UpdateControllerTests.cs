using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.AutoUpdate;
using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Installation;
using GitHubReleaseUpdater.LastCheck;
using GitHubReleaseUpdater.Verification;
using GitHubReleaseUpdater.Versioning;
using System.Security.Cryptography;

namespace GitHubReleaseUpdater.Tests;

/// <summary>
/// Tests for <see cref="UpdateController"/>, over an in-memory GitHub client, a recording launcher and presenter, and a
/// clock the test moves by hand.
/// </summary>
public sealed class UpdateControllerTests : IDisposable
{
    /// <summary>
    /// File name of the installer asset in every release.
    /// </summary>
    private const string Installer = "setup.exe";

    /// <summary>
    /// The download directory, unique per test.
    /// </summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ghru-ctl-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Stands in for GitHub.
    /// </summary>
    private readonly FakeReleaseClient _client = new();

    /// <summary>
    /// Records what the user would have been told or asked.
    /// </summary>
    private readonly RecordingPresenter _presenter = new();

    /// <summary>
    /// Records the installers that were started.
    /// </summary>
    private readonly RecordingLauncher _launcher = new();

    /// <summary>
    /// The clock the controller and the updater use.
    /// </summary>
    private readonly ManualTimeProvider _time = new();

    /// <summary>
    /// The store the updater throttles with unless a test passes another.
    /// </summary>
    private readonly InMemoryLastCheckStore _store = new();

    /// <summary>
    /// Errors the controller passed to its error callback.
    /// </summary>
    private readonly List<Exception> _errors = [];

    /// <summary>
    /// Publishes version 2.0.0 with an installer of ten zero bytes.
    /// </summary>
    public UpdateControllerTests()
    {
        _client.AssetBytes[Installer] = new byte[10];
        PublishRelease("v2.0.0");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// An automatic check that finds a version announces it, once.
    /// </summary>
    [Fact]
    public async Task Automatic_check_that_finds_a_version_announces_it_once()
    {
        var controller = Create();

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Equal(SemanticVersion.Parse("2.0.0"), controller.Available?.Version);
        Assert.Single(_presenter.Announced);
        Assert.Equal(0, _presenter.Confirmations);
    }

    /// <summary>
    /// A newer version found later is announced again.
    /// </summary>
    [Fact]
    public async Task A_newer_version_found_later_is_announced_again()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        PublishRelease("v3.0.0");
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, _presenter.Announced.Count);
    }

    /// <summary>
    /// An automatic check that fails says nothing to the user.
    /// </summary>
    [Fact]
    public async Task Automatic_check_that_fails_is_silent()
    {
        _client.ThrowOnFetch = new HttpRequestException("offline");
        var controller = Create();

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Failed, controller.State);
        Assert.Empty(_presenter.Failures);
    }

    /// <summary>
    /// A manual check that fails passes the error to the presenter.
    /// </summary>
    [Fact]
    public async Task Manual_check_that_fails_reports_the_error()
    {
        var error = new HttpRequestException("offline");
        _client.ThrowOnFetch = error;
        var controller = Create();

        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        Assert.Same(error, Assert.Single(_presenter.Failures));
    }

    /// <summary>
    /// A failed check keeps the version found before.
    /// </summary>
    [Fact]
    public async Task A_failed_check_keeps_the_version_found_before()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        _client.ThrowOnFetch = new HttpRequestException("offline");

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.NotNull(controller.Available);
    }

    /// <summary>
    /// A newer release without an acceptable installer counts as a failed check.
    /// </summary>
    [Fact]
    public async Task Release_without_an_acceptable_installer_counts_as_a_failed_check()
    {
        _client.Latest = TestData.Release("v2.0.0", "notes.txt");
        var controller = Create();

        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Failed, controller.State);
        Assert.IsType<AssetNotFoundException>(Assert.Single(_presenter.Failures));
    }

    /// <summary>
    /// A manual check that finds nothing says so.
    /// </summary>
    [Fact]
    public async Task Manual_check_that_finds_nothing_says_so()
    {
        _client.Latest = TestData.Release("v1.0.0", Installer);
        var controller = Create();

        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, controller.State);
        Assert.Null(controller.Available);
        Assert.Equal(1, _presenter.UpToDateReports);
    }

    /// <summary>
    /// A manual check ignores both the throttling and a skipped version.
    /// </summary>
    [Fact]
    public async Task Manual_check_bypasses_throttling_and_a_skipped_version()
    {
        await _store.SetSkippedVersionAsync(SemanticVersion.Parse("2.0.0"), TestContext.Current.CancellationToken);
        var controller = Create(throttle: true);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        var afterAutomatic = controller.State;
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        var afterThrottled = controller.State;

        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, afterAutomatic);
        Assert.Equal(UpdateState.UpToDate, afterThrottled);
        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Equal(1, _presenter.Confirmations);
    }

    /// <summary>
    /// A check skipped for being too recent keeps what was known before.
    /// </summary>
    [Fact]
    public async Task Throttled_check_keeps_what_was_known_before()
    {
        var controller = Create(throttle: true);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.NotNull(controller.Available);
    }

    /// <summary>
    /// A manual check that finds a version asks right away and installs when the user agrees.
    /// </summary>
    [Fact]
    public async Task Manual_check_that_finds_a_version_asks_and_installs_when_the_user_agrees()
    {
        var controller = Create();

        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Confirmations);
        var launched = Assert.Single(_launcher.Launched);
        Assert.Equal(Installer, Path.GetFileName(launched.Path));
        Assert.Equal(1, _presenter.InstallerStartedCount);
        Assert.Equal(1, _presenter.Downloading);
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A manual check requested while an automatic one runs is made when that one ends.
    /// </summary>
    [Fact]
    public async Task Manual_check_requested_during_an_automatic_one_is_made_afterwards()
    {
        var controller = Create();
        var requested = false;
        controller.Changed += (_, _) =>
        {
            if (controller.State == UpdateState.Checking && !requested)
            {
                requested = true;
                _ = controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);
            }
        };

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Confirmations);
        Assert.Single(_launcher.Launched);
        Assert.Single(_presenter.Announced);
    }

    /// <summary>
    /// The launch options given to the controller reach the launcher.
    /// </summary>
    [Fact]
    public async Task Installer_launch_options_are_passed_on()
    {
        var controller = Create(launchOptions: InstallerLaunchOptions.InnoSetupSilent);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        var options = Assert.Single(_launcher.Launched).Options;
        Assert.Equal(InstallerLaunchOptions.InnoSetupSilent.Arguments, options.Arguments);
        Assert.False(options.RequireVerified);
    }

    /// <summary>
    /// By default an installer whose checksum could not be verified is not started.
    /// </summary>
    [Fact]
    public async Task Unverified_download_is_not_launched_by_default()
    {
        var controller = Create(requireVerified: true);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_launcher.Launched);
        Assert.IsType<UpdaterException>(Assert.Single(_presenter.InstallFailures));
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A download whose checksum matches is started even when verification is required, with the other launch options
    /// kept.
    /// </summary>
    [Fact]
    public async Task Verified_download_is_launched_when_verification_is_required()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[10]));
        var controller = Create(requireVerified: true, checksums: new StaticChecksumProvider(hash), launchOptions: InstallerLaunchOptions.InnoSetupSilent);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        var options = Assert.Single(_launcher.Launched).Options;
        Assert.True(options.RequireVerified);
        Assert.Equal(InstallerLaunchOptions.InnoSetupSilent.Arguments, options.Arguments);
        Assert.Empty(_presenter.InstallFailures);
    }

    /// <summary>
    /// When the user declines, nothing is downloaded.
    /// </summary>
    [Fact]
    public async Task Declined_installation_downloads_nothing()
    {
        _presenter.Agree = false;
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_launcher.Launched);
        Assert.Equal(0, _presenter.InstallerStartedCount);
        Assert.Equal(UpdateState.Available, controller.State);
        Assert.False(Directory.Exists(_directory));
    }

    /// <summary>
    /// A failed installation is reported and the version stays available.
    /// </summary>
    [Fact]
    public async Task Failed_installation_is_reported_and_the_version_stays_available()
    {
        _launcher.Failure = new InstallerLaunchException("cannot start", "x", isUserCancelled: false);
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Same(_launcher.Failure, Assert.Single(_presenter.InstallFailures));
        Assert.Equal(0, _presenter.InstallerStartedCount);
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A declined elevation prompt is not a failure.
    /// </summary>
    [Fact]
    public async Task Declined_elevation_prompt_is_not_reported_as_a_failure()
    {
        _launcher.Failure = new InstallerLaunchException("cancelled", "x", isUserCancelled: true);
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_presenter.InstallFailures);
        Assert.Equal(0, _presenter.InstallerStartedCount);
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// Installing without a found version does nothing.
    /// </summary>
    [Fact]
    public async Task Install_without_a_found_version_does_nothing()
    {
        var controller = Create();

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _presenter.Confirmations);
        Assert.Empty(_launcher.Launched);
    }

    /// <summary>
    /// A second installation while the user is being asked does nothing.
    /// </summary>
    [Fact]
    public async Task Second_install_while_the_user_is_being_asked_does_nothing()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource<bool>();
        _presenter.ConfirmGate = gate.Task;

        var first = controller.InstallAsync(TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);
        gate.SetResult(true);
        await first;

        Assert.Equal(1, _presenter.Confirmations);
        Assert.Single(_launcher.Launched);
    }

    /// <summary>
    /// A presenter that throws while the download starts does not strand the controller in the installing state.
    /// </summary>
    [Fact]
    public async Task Presenter_that_throws_when_the_download_starts_does_not_strand_the_controller()
    {
        _presenter.DownloadingFailure = new InvalidOperationException("balloon failed");
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Same(_presenter.DownloadingFailure, Assert.Single(_errors));
        Assert.Single(_launcher.Launched);
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A handler of Changed that throws does not leave the controller in the checking state.
    /// </summary>
    [Fact]
    public async Task Changed_handler_that_throws_does_not_strand_the_controller()
    {
        var controller = Create();
        var failure = new InvalidOperationException("handler failed");
        controller.Changed += (_, _) => throw failure;

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Contains(failure, _errors);
    }

    /// <summary>
    /// A presenter that throws when asked whether to install counts as a no and is reported.
    /// </summary>
    [Fact]
    public async Task Presenter_that_throws_when_asked_counts_as_a_no()
    {
        _presenter.ConfirmFailure = new InvalidOperationException("dialog failed");
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_launcher.Launched);
        Assert.Same(_presenter.ConfirmFailure, Assert.Single(_errors));
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A failure to record the time of the check is passed to the error callback and does not spoil the check.
    /// </summary>
    [Fact]
    public async Task Failure_to_record_the_check_is_reported_but_the_check_counts()
    {
        var controller = Create(store: new WriteFailingStore());

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.IsType<IOException>(Assert.Single(_errors));
    }

    /// <summary>
    /// Changed is raised for every change of state.
    /// </summary>
    [Fact]
    public async Task Changed_is_raised_for_every_state_change()
    {
        var controller = Create();
        var states = new List<UpdateState>();
        controller.Changed += (_, _) => states.Add(controller.State);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal([UpdateState.Checking, UpdateState.Available], states);
    }

    /// <summary>
    /// A cancelled check restores the previous state and throws.
    /// </summary>
    [Fact]
    public async Task Cancelled_check_restores_the_previous_state_and_throws()
    {
        var controller = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _client.ThrowOnFetch = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.CheckAsync(manual: false, cts.Token));

        Assert.Equal(UpdateState.Idle, controller.State);
    }

    /// <summary>
    /// The loop checks after the initial delay and then at every interval.
    /// </summary>
    [Fact]
    public async Task Run_checks_after_the_initial_delay_and_then_every_interval()
    {
        var controller = Create();
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(29));
        var tooEarly = _presenter.Announced.Count;
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => controller.State == UpdateState.Available && _time.PendingTimers == 1);
        PublishRelease("v3.0.0");
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => _presenter.Announced.Count == 2);

        Assert.Equal(0, tooEarly);
        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// The loop skips the rounds in which automatic checks are off.
    /// </summary>
    [Fact]
    public async Task Run_skips_rounds_in_which_automatic_checks_are_off()
    {
        var enabled = false;
        var controller = Create(isAutoCheckEnabled: () => enabled);
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => _time.PendingTimers == 1);
        var whileOff = controller.State;
        enabled = true;
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => controller.State == UpdateState.Available);

        Assert.Equal(UpdateState.Idle, whileOff);
        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// A delegate that decides about automatic checks and throws is reported, and the loop carries on.
    /// </summary>
    [Fact]
    public async Task Run_survives_an_auto_check_delegate_that_throws()
    {
        var calls = 0;
        var controller = Create(isAutoCheckEnabled: () => ++calls == 1 ? throw new InvalidOperationException("settings gone") : true);
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => _errors.Count == 1 && _time.PendingTimers == 1);
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => controller.State == UpdateState.Available);

        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// A presenter that throws, even with a cancellation, does not end the loop and is reported.
    /// </summary>
    /// <param name="cancellation">True to throw an OperationCanceledException instead of an ordinary exception.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_survives_a_presenter_that_throws_and_reports_it(bool cancellation)
    {
        _presenter.AnnounceFailure = cancellation ? new OperationCanceledException() : new InvalidOperationException("boom");
        var controller = Create();
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => _errors.Count == 1 && _time.PendingTimers == 1);
        _presenter.AnnounceFailure = null;
        PublishRelease("v3.0.0");
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => _presenter.Announced.Count == 1);

        Assert.Same(_presenter.LastAnnounceFailure, Assert.Single(_errors));
        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// The loop returns without throwing when cancelled.
    /// </summary>
    [Fact]
    public async Task Run_returns_without_throwing_when_cancelled()
    {
        var controller = Create();
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);

        await cts.CancelAsync();

        await loop;
        Assert.True(loop.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A check interval that is not positive is refused.
    /// </summary>
    [Fact]
    public void Constructor_rejects_a_non_positive_interval()
    {
        var updater = new ReleaseUpdater(Options(throttle: false, _store, NoChecksumProvider.Instance), _client);

        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateController(updater, _presenter, new UpdateControllerOptions { CheckInterval = TimeSpan.Zero }));
    }

    /// <summary>
    /// Makes the latest release the one with the given tag.
    /// </summary>
    /// <param name="tag">The tag of the release.</param>
    private void PublishRelease(string tag)
    {
        _client.Latest = TestData.Release(tag, Installer);
    }

    /// <summary>
    /// Creates a controller over the fakes.
    /// </summary>
    /// <param name="throttle">Whether the updater throttles automatic checks.</param>
    /// <param name="launchOptions">The launch options for the installer.</param>
    /// <param name="isAutoCheckEnabled">Decides whether an automatic check is made.</param>
    /// <param name="requireVerified">Whether an unverified download must not be launched; off by default here because
    /// the fake release has no checksum.</param>
    /// <param name="checksums">The checksum provider; none by default.</param>
    /// <param name="store">The last-check store; the in-memory one by default.</param>
    /// <returns>The controller.</returns>
    private UpdateController Create(
        bool throttle = false,
        InstallerLaunchOptions? launchOptions = null,
        Func<bool>? isAutoCheckEnabled = null,
        bool requireVerified = false,
        IChecksumProvider? checksums = null,
        ILastCheckStore? store = null)
    {
        return new UpdateController(
            new ReleaseUpdater(Options(throttle, store ?? _store, checksums ?? NoChecksumProvider.Instance), _client),
            _presenter,
            new UpdateControllerOptions
            {
                DownloadDirectory = _directory,
                InstallerLaunchOptions = launchOptions,
                IsAutoCheckEnabled = isAutoCheckEnabled,
                RequireVerifiedDownload = requireVerified,
                OnError = _errors.Add,
                TimeProvider = _time,
            });
    }

    /// <summary>
    /// Builds the updater options used by the tests.
    /// </summary>
    /// <param name="throttle">Whether to throttle checks to once in 24 hours.</param>
    /// <param name="store">The last-check store.</param>
    /// <param name="checksums">The checksum provider.</param>
    /// <returns>The options.</returns>
    private UpdaterOptions Options(bool throttle, ILastCheckStore store, IChecksumProvider checksums) => new()
    {
        Owner = "o",
        Repo = "r",
        CurrentVersion = "1.0.0",
        AssetSelector = new DelegateAssetSelector(a => a.Name == Installer),
        ChecksumProvider = checksums,
        InstallerLauncher = _launcher,
        LastCheckStore = store,
        MinimumCheckInterval = throttle ? TimeSpan.FromHours(24) : null,
        TimeProvider = _time,
    };

    /// <summary>
    /// Waits until a condition holds, for at most five seconds, and fails the test if it does not.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The condition did not become true in time.");
    }

    /// <summary>
    /// A clock that only moves when the test says so and fires the timers that fall due.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        /// <summary>
        /// The timers that have not fired yet.
        /// </summary>
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Source)> _timers = [];

        /// <summary>
        /// The current time.
        /// </summary>
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Gets how many timers are waiting to fire, which tells a test that the code under test has started its wait.
        /// </summary>
        public int PendingTimers
        {
            get
            {
                lock (_timers)
                {
                    return _timers.Count;
                }
            }
        }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            lock (_timers)
            {
                return _now;
            }
        }

        /// <summary>
        /// Moves the clock forward and fires the timers that are due.
        /// </summary>
        /// <param name="by">How far to move.</param>
        public void Advance(TimeSpan by)
        {
            List<TaskCompletionSource> due;
            lock (_timers)
            {
                _now += by;
                due = _timers.Where(t => t.Due <= _now).Select(t => t.Source).ToList();
                _timers.RemoveAll(t => t.Due <= _now);
            }

            foreach (var source in due)
            {
                source.TrySetResult();
            }
        }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            return new ManualTimer(this, callback, state, dueTime);
        }

        /// <summary>
        /// A one-shot timer that fires when <see cref="Advance"/> passes its due time.
        /// </summary>
        private sealed class ManualTimer : ITimer
        {
            /// <summary>
            /// The clock the timer belongs to.
            /// </summary>
            private readonly ManualTimeProvider _owner;

            /// <summary>
            /// Completed when the timer fires.
            /// </summary>
            private readonly TaskCompletionSource _source = new();

            /// <summary>
            /// Starts the timer.
            /// </summary>
            /// <param name="owner">The clock the timer belongs to.</param>
            /// <param name="callback">Called when the timer fires.</param>
            /// <param name="state">Passed to the callback.</param>
            /// <param name="dueTime">How long from now the timer fires.</param>
            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
            {
                _owner = owner;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return;
                }

                lock (owner._timers)
                {
                    owner._timers.Add((owner._now + dueTime, _source));
                }

                _source.Task.ContinueWith(_ => callback(state), TaskScheduler.Default);
            }

            /// <inheritdoc />
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            /// <inheritdoc />
            public void Dispose()
            {
                lock (_owner._timers)
                {
                    _owner._timers.RemoveAll(t => t.Source == _source);
                }
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A launcher that records the installers it was asked to start instead of starting them.
    /// </summary>
    private sealed class RecordingLauncher : IInstallerLauncher
    {
        /// <summary>
        /// Gets the installers started, with the options they were started with.
        /// </summary>
        public List<(string Path, InstallerLaunchOptions Options)> Launched { get; } = [];

        /// <summary>
        /// Gets or sets an exception to throw instead of starting the installer.
        /// </summary>
        public Exception? Failure { get; set; }

        /// <inheritdoc />
        public InstallerLaunchResult Launch(string installerPath, InstallerLaunchOptions options)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Launched.Add((installerPath, options));
            return new InstallerLaunchResult(installerPath, null);
        }
    }

    /// <summary>
    /// A last-check store that reads nothing and fails to write.
    /// </summary>
    private sealed class WriteFailingStore : ILastCheckStore
    {
        /// <inheritdoc />
        public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateTimeOffset?>(null);

        /// <inheritdoc />
        public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<SemanticVersion?>(null);

        /// <inheritdoc />
        public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default) => throw new IOException("disk full");

        /// <inheritdoc />
        public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A presenter that records what it was told and answers as the test says.
    /// </summary>
    private sealed class RecordingPresenter : IUpdatePresenter
    {
        /// <summary>
        /// Gets or sets whether the user agrees to install.
        /// </summary>
        public bool Agree { get; set; } = true;

        /// <summary>
        /// Gets or sets a task the confirmation waits for, to keep the user "being asked".
        /// </summary>
        public Task<bool>? ConfirmGate { get; set; }

        /// <summary>
        /// Gets or sets an exception thrown when announcing.
        /// </summary>
        public Exception? AnnounceFailure { get; set; }

        /// <summary>
        /// Gets or sets an exception thrown when the download starts.
        /// </summary>
        public Exception? DownloadingFailure { get; set; }

        /// <summary>
        /// Gets or sets an exception thrown when asking to install.
        /// </summary>
        public Exception? ConfirmFailure { get; set; }

        /// <summary>
        /// Gets the last exception thrown when announcing.
        /// </summary>
        public Exception? LastAnnounceFailure { get; private set; }

        /// <summary>
        /// Gets how many times the user was asked to install.
        /// </summary>
        public int Confirmations { get; private set; }

        /// <summary>
        /// Gets how many times the user was told that nothing newer exists.
        /// </summary>
        public int UpToDateReports { get; private set; }

        /// <summary>
        /// Gets how many times the user was told that a download started.
        /// </summary>
        public int Downloading { get; private set; }

        /// <summary>
        /// Gets how many times the application was told that the installer started.
        /// </summary>
        public int InstallerStartedCount { get; private set; }

        /// <summary>
        /// Gets the versions announced.
        /// </summary>
        public List<AvailableUpdate> Announced { get; } = [];

        /// <summary>
        /// Gets the errors of failed checks that were reported.
        /// </summary>
        public List<Exception?> Failures { get; } = [];

        /// <summary>
        /// Gets the errors of failed installations that were reported.
        /// </summary>
        public List<Exception> InstallFailures { get; } = [];

        /// <inheritdoc />
        public void Announce(AvailableUpdate update)
        {
            if (AnnounceFailure is not null)
            {
                LastAnnounceFailure = AnnounceFailure;
                throw AnnounceFailure;
            }

            Announced.Add(update);
        }

        /// <inheritdoc />
        public void ReportUpToDate() => UpToDateReports++;

        /// <inheritdoc />
        public void ReportCheckFailed(Exception? error) => Failures.Add(error);

        /// <inheritdoc />
        public async ValueTask<bool> ConfirmInstallAsync(AvailableUpdate update, SemanticVersion currentVersion)
        {
            Confirmations++;
            if (ConfirmFailure is not null)
            {
                throw ConfirmFailure;
            }

            if (ConfirmGate is not null)
            {
                await ConfirmGate;
            }

            return Agree;
        }

        /// <inheritdoc />
        public void ReportDownloading(AvailableUpdate update)
        {
            Downloading++;
            if (DownloadingFailure is not null)
            {
                throw DownloadingFailure;
            }
        }

        /// <inheritdoc />
        public void ReportProgress(AvailableUpdate update, DownloadProgress progress)
        {
        }

        /// <inheritdoc />
        public void ReportInstallFailed(AvailableUpdate update, Exception error) => InstallFailures.Add(error);

        /// <inheritdoc />
        public void InstallerStarted(AvailableUpdate update) => InstallerStartedCount++;
    }
}
