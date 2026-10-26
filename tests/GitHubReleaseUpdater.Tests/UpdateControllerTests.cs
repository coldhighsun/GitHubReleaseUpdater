using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.AutoUpdate;
using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub.Models;
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
    /// A newer release without an acceptable installer stays known, and a manual check tells the user.
    /// </summary>
    [Fact]
    public async Task Release_without_an_acceptable_installer_stays_known_and_is_reported_to_a_manual_check()
    {
        _client.Latest = TestData.Release("v2.0.0", "notes.txt");
        var controller = Create();

        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.False(controller.HasInstaller);
        Assert.Equal(SemanticVersion.Parse("2.0.0"), controller.Available?.Version);
        Assert.IsType<AssetNotFoundException>(Assert.Single(_presenter.Failures));
    }

    /// <summary>
    /// Without an installer an automatic check announces nothing, and downloading or installing starts nothing but reports why.
    /// </summary>
    [Fact]
    public async Task Release_without_an_installer_is_not_announced_and_cannot_be_installed()
    {
        _client.Latest = TestData.Release("v2.0.0", "notes.txt");
        var controller = Create();

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.DownloadAsync(TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Empty(_presenter.Announced);
        Assert.Empty(_presenter.Failures);
        Assert.Equal(0, _presenter.Confirmations);
        Assert.Empty(_launcher.Launched);
        // Asking for the download or the installation is not met with silence: each is reported as a failure.
        Assert.Equal(2, _presenter.InstallFailures.Count);
        Assert.All(_presenter.InstallFailures, error => Assert.IsType<AssetNotFoundException>(error));
    }

    /// <summary>
    /// With launching turned off, installing downloads the package and stops there, asking only once.
    /// </summary>
    [Fact]
    public async Task Installing_without_launching_only_downloads()
    {
        var controller = Create(launchInstaller: false);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);
        Assert.True(File.Exists(controller.DownloadedFile));
        Assert.Empty(_launcher.Launched);
        Assert.Equal(1, _presenter.Confirmations);
        Assert.Equal(0, _presenter.InstallerStartedCount);
        Assert.Equal([controller.DownloadedFile!], _presenter.PackagesDownloaded);
    }

    /// <summary>
    /// A download made ahead is reported as left for the user, and an installation that starts the installer is not.
    /// </summary>
    [Fact]
    public async Task Package_downloaded_is_reported_for_a_download_but_not_for_a_started_installer()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal([controller.DownloadedFile!], _presenter.PackagesDownloaded);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Single(_presenter.PackagesDownloaded);
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
        Assert.Equal(UpdateState.Downloaded, controller.State);
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
        Assert.Equal(UpdateState.Downloaded, controller.State);
    }

    /// <summary>
    /// A download made ahead of the installation is not made again.
    /// </summary>
    [Fact]
    public async Task Downloaded_installer_is_started_without_downloading_again()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);
        Assert.True(File.Exists(controller.DownloadedFile));

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Downloading);
        Assert.Single(_launcher.Launched);
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A check that finds the same version again keeps the downloaded installer.
    /// </summary>
    [Fact]
    public async Task Recheck_of_the_same_version_stays_downloaded()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.DownloadAsync(TestContext.Current.CancellationToken);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);
    }

    /// <summary>
    /// The application gets ready before the installer starts, and a failure of that does not stop the installation.
    /// </summary>
    [Fact]
    public async Task Before_install_runs_first_and_its_failure_does_not_stop_the_installation()
    {
        var order = new List<string>();
        var controller = Create(beforeInstall: _ =>
        {
            order.Add(_launcher.Launched.Count == 0 ? "before" : "late");
            throw new IOException("cannot save");
        });
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["before"], order);
        Assert.Single(_launcher.Launched);
        Assert.Contains(_errors, e => e is IOException);
    }

    /// <summary>
    /// A watched installer that ends while the application runs offers the downloaded installer again.
    /// </summary>
    [Fact]
    public async Task Watched_installer_that_ends_returns_to_downloaded()
    {
        _launcher.ProcessId = 42;
        var controller = Create(watchInstaller: true);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);
        Assert.Equal(1, _presenter.InstallerEndedCount);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Downloading);
        Assert.Equal(2, _launcher.Launched.Count);
    }

    /// <summary>
    /// Files of earlier downloads are deleted before a new download when that is enabled, and kept otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Old_download_files_are_deleted_only_when_enabled(bool clean)
    {
        Directory.CreateDirectory(_directory);
        var stray = Path.Combine(_directory, "old-setup.exe");
        await File.WriteAllBytesAsync(stray, [1], TestContext.Current.CancellationToken);
        var controller = Create(cleanDirectory: clean);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(!clean, File.Exists(stray));
    }

    /// <summary>
    /// While a newer release is pending, a store that can remember it reports no check, so the next one is not throttled.
    /// </summary>
    [Fact]
    public async Task Pending_update_is_recorded_in_the_store()
    {
        var controller = Create(throttle: true);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Null(await _store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Extra arguments are appended to the preset's, and the original is untouched.
    /// </summary>
    [Fact]
    public void With_arguments_appends_to_a_preset()
    {
        var options = InstallerLaunchOptions.InnoSetupSilent.WithArguments("/RELAUNCH=1");

        Assert.Equal("/SILENT /SP- /NORESTART /RELAUNCH=1", options.Arguments);
        Assert.Equal("/SILENT /SP- /NORESTART", InstallerLaunchOptions.InnoSetupSilent.Arguments);
    }

    /// <summary>
    /// Once the installer was started, the downloaded file is no longer offered, so later checks stay at Available.
    /// </summary>
    [Fact]
    public async Task Started_installer_leaves_no_download_behind()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.DownloadAsync(TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Null(controller.DownloadedFile);
    }

    /// <summary>
    /// Waits that are negative or too long are refused when the controller is created.
    /// </summary>
    [Theory]
    [InlineData(-1, 10)]
    [InlineData(30, -5)]
    [InlineData(3600 * 24 * 10, 10)]
    [InlineData(30, 3600 * 24 * 60)]
    public void Negative_waits_are_refused(int graceSeconds, int timeoutSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            gracePeriod: TimeSpan.FromSeconds(graceSeconds),
            beforeInstallTimeout: TimeSpan.FromSeconds(timeoutSeconds)));
    }

    /// <summary>
    /// A download whose asset was replaced on the release is not reused.
    /// </summary>
    [Fact]
    public async Task Replaced_asset_is_downloaded_again()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.DownloadAsync(TestContext.Current.CancellationToken);
        _client.Latest = new GitHubRelease
        {
            Id = 1,
            TagName = "v2.0.0",
            Assets = [new GitHubAsset { Id = 99, Name = Installer, Size = 10, BrowserDownloadUrl = "https://github.com/o/r/releases/download/v2.0.0/" + Installer }],
        };

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Null(controller.DownloadedFile);
    }

    /// <summary>
    /// A downloaded version can be skipped, which forgets the download.
    /// </summary>
    [Fact]
    public async Task Downloaded_version_can_be_skipped()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.DownloadAsync(TestContext.Current.CancellationToken);

        var skipped = await controller.SkipVersionAsync(TestContext.Current.CancellationToken);

        Assert.True(skipped);
        Assert.Equal(UpdateState.UpToDate, controller.State);
        Assert.Null(controller.DownloadedFile);
    }

    /// <summary>
    /// A preparation that never ends delays the installer only until its timeout.
    /// </summary>
    [Fact]
    public async Task Before_install_that_hangs_is_given_up_after_its_timeout()
    {
        var controller = Create(beforeInstall: _ => new TaskCompletionSource().Task);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        var install = controller.InstallAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromSeconds(11));
            return install.IsCompleted;
        });
        await install;

        Assert.Single(_launcher.Launched);
        Assert.Contains(_errors, e => e is TimeoutException);
    }

    /// <summary>
    /// An installer without a process to watch is given the longer grace period and then offered again.
    /// </summary>
    [Fact]
    public async Task Watched_installer_without_a_process_is_offered_again()
    {
        var controller = Create(watchInstaller: true);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);
        Assert.Equal(1, _presenter.InstallerEndedCount);
    }

    /// <summary>
    /// Cancelling while the installer is watched ends the wait and leaves the controller usable.
    /// </summary>
    [Fact]
    public async Task Cancelling_while_the_installer_is_watched_ends_the_wait()
    {
        _launcher.ProcessId = 42;
        using var cts = new CancellationTokenSource();
        var controller = Create(watchInstaller: true, waitForExit: _ => new TaskCompletionSource().Task);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        var install = controller.InstallAsync(cts.Token);
        await WaitUntilAsync(() => _launcher.Launched.Count == 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.NotEqual(UpdateState.Installing, controller.State);
        Assert.Equal(0, _presenter.InstallerEndedCount);
    }

    /// <summary>
    /// With cleaning enabled, a check that finds the application up to date deletes what earlier downloads left.
    /// </summary>
    [Fact]
    public async Task Up_to_date_check_deletes_leftovers_when_enabled()
    {
        PublishRelease("v1.0.0");
        Directory.CreateDirectory(_directory);
        var stray = Path.Combine(_directory, "old-setup.exe");
        await File.WriteAllBytesAsync(stray, [1], TestContext.Current.CancellationToken);
        var controller = Create(cleanDirectory: true);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, controller.State);
        Assert.False(File.Exists(stray));
    }

    /// <summary>
    /// A download whose asset changed in any identifying field is not reused.
    /// </summary>
    [Theory]
    [InlineData("size")]
    [InlineData("digest")]
    [InlineData("created")]
    public async Task Changed_asset_fields_are_downloaded_again(string field)
    {
        var controller = Create();
        _client.Latest = Replaced(1, 10, null, null);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Downloaded, controller.State);

        _client.Latest = field switch
        {
            "size" => Replaced(1, 11, null, null),
            "digest" => Replaced(1, 10, "sha256:" + new string('a', 64), null),
            _ => Replaced(1, 10, null, DateTimeOffset.UnixEpoch),
        };
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Null(controller.DownloadedFile);
    }

    /// <summary>
    /// A declined elevation prompt keeps the download, so asking again does not download again.
    /// </summary>
    [Fact]
    public async Task Declined_elevation_prompt_keeps_the_download_for_the_next_try()
    {
        _launcher.Failure = new InstallerLaunchException("cancelled", "x", isUserCancelled: true);
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);

        _launcher.Failure = null;
        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Downloading);
        Assert.Single(_launcher.Launched);
    }

    /// <summary>
    /// Skipping a downloaded version deletes its installer.
    /// </summary>
    [Fact]
    public async Task Skipping_a_downloaded_version_deletes_the_file()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.DownloadAsync(TestContext.Current.CancellationToken);
        var file = controller.DownloadedFile!;

        await controller.SkipVersionAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(file));
    }

    /// <summary>
    /// Builds version 2.0.0 with an installer asset of the given identity.
    /// </summary>
    private static GitHubRelease Replaced(long id, long size, string? digest, DateTimeOffset? created) => new()
    {
        Id = 1,
        TagName = "v2.0.0",
        Assets = [new GitHubAsset { Id = id, Name = Installer, Size = size, Digest = digest, CreatedAt = created, BrowserDownloadUrl = "https://github.com/o/r/releases/download/v2.0.0/" + Installer }],
    };

    /// <summary>
    /// A launch that fails for a reason that is not the file's fault keeps the verified download for another try.
    /// </summary>
    [Fact]
    public async Task Failed_launch_keeps_the_download_for_the_next_try()
    {
        _launcher.Failure = new InstallerLaunchException("busy", "x", isUserCancelled: false);
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Single(_presenter.InstallFailures);
        Assert.Equal(UpdateState.Downloaded, controller.State);

        _launcher.Failure = null;
        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Downloading);
        Assert.Single(_launcher.Launched);
    }

    /// <summary>
    /// Skipping a version clears the pending flag, so the next check is throttled again.
    /// </summary>
    [Fact]
    public async Task Skipping_clears_the_pending_flag()
    {
        var controller = Create(throttle: true);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        Assert.Null(await _store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));

        await controller.SkipVersionAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await _store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A file where the download directory should be is not listed, and the check still completes.
    /// </summary>
    [Fact]
    public async Task File_in_place_of_the_download_directory_does_not_fail_the_check()
    {
        PublishRelease("v1.0.0");
        await File.WriteAllBytesAsync(_directory, [1], TestContext.Current.CancellationToken);
        var controller = Create(cleanDirectory: true);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, controller.State);
        File.Delete(_directory);
    }

    /// <summary>
    /// A package that could not be verified is not offered when nothing is launched, since the user would unpack it.
    /// </summary>
    [Fact]
    public async Task Unverified_package_is_refused_when_not_launching()
    {
        var controller = Create(launchInstaller: false, requireVerified: true);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.IsType<UpdaterException>(Assert.Single(_presenter.InstallFailures));
        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Null(controller.DownloadedFile);
        Assert.Empty(_presenter.PackagesDownloaded);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    /// <summary>
    /// When the downloaded package was removed, installing downloads it again instead of doing nothing.
    /// </summary>
    [Fact]
    public async Task Removed_package_is_downloaded_again_when_not_launching()
    {
        var controller = Create(launchInstaller: false);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);
        File.Delete(controller.DownloadedFile!);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);
        Assert.True(File.Exists(controller.DownloadedFile));
        Assert.Equal(2, _presenter.Downloading);
    }

    /// <summary>
    /// A release without an installer does not mark an update as pending, so later checks stay throttled.
    /// </summary>
    [Fact]
    public async Task Release_without_an_installer_leaves_the_check_throttled()
    {
        _client.Latest = TestData.Release("v2.0.0", "notes.txt");
        var controller = Create(throttle: true);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.NotNull(await _store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An installer that cannot be watched is reported and given ten times the grace period before it is taken to have ended.
    /// </summary>
    /// <returns>
    /// A task that completes when the test is done.
    /// </returns>
    [Fact]
    public async Task Installer_that_cannot_be_watched_is_given_ten_times_the_grace_period()
    {
        _launcher.ProcessId = 42;
        var controller = Create(
            watchInstaller: true,
            gracePeriod: TimeSpan.FromSeconds(1),
            waitForExit: _ => Task.FromException(new UnauthorizedAccessException()));
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        var install = controller.InstallAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _errors.Count > 0 && _time.PendingTimers == 1);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(install.IsCompleted);
        Assert.Equal(UpdateState.Installing, controller.State);

        _time.Advance(TimeSpan.FromSeconds(9));
        await install;

        Assert.Equal(UpdateState.Downloaded, controller.State);
        Assert.Contains(_errors, e => e is UnauthorizedAccessException);
        Assert.Equal(1, _presenter.InstallerEndedCount);
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
    /// An operation cancelled by something other than the caller's token, such as a timeout, is a failed installation.
    /// </summary>
    [Fact]
    public async Task Timeout_during_the_installation_is_reported_as_a_failure()
    {
        var controller = Create(downloadRetries: 0);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        _client.ThrowOnOpen = new TaskCanceledException("timed out");

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Single(_presenter.InstallFailures);
        Assert.Equal(1, _client.OpenCalls);
        Assert.Empty(_launcher.Launched);
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A version the user was asked about in a manual check is not announced again by an automatic one.
    /// </summary>
    [Fact]
    public async Task Version_asked_about_in_a_manual_check_is_not_announced_afterwards()
    {
        _presenter.Agree = false;
        var controller = Create();
        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Confirmations);
        Assert.Empty(_presenter.Announced);
    }

    /// <summary>
    /// A manual check queued behind an automatic one that is then cancelled goes with it.
    /// </summary>
    [Fact]
    public async Task Queued_manual_check_is_dropped_when_the_automatic_one_is_cancelled()
    {
        var controller = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var calls = RequestManualChecksDuringFirstCheck(controller, 1, onRequested: () => _client.ThrowOnFetch = new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.CheckAsync(manual: false, cts.Token));

        Assert.Equal(0, _presenter.Confirmations);
        Assert.Empty(_launcher.Launched);
        Assert.Equal(UpdateState.Idle, controller.State);
        Assert.All(calls, call => Assert.True(call.IsCompletedSuccessfully));
    }

    /// <summary>
    /// Several manual checks requested during one automatic check are made as one.
    /// </summary>
    [Fact]
    public async Task Several_manual_checks_requested_during_an_automatic_one_are_made_as_one()
    {
        var controller = Create();
        var calls = RequestManualChecksDuringFirstCheck(controller, 2);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Confirmations);
        Assert.All(calls, call => Assert.True(call.IsCompletedSuccessfully));
    }

    /// <summary>
    /// The queued check runs under the token of the call that was running, not the tokens of the callers that asked.
    /// </summary>
    [Fact]
    public async Task Queued_manual_check_ignores_the_tokens_of_its_callers()
    {
        var controller = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var calls = RequestManualChecksDuringFirstCheck(controller, 1, cts.Token);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Confirmations);
        Assert.All(calls, call => Assert.True(call.IsCompletedSuccessfully));
    }

    /// <summary>
    /// A skip that is being written does not undo a newer version found meanwhile.
    /// </summary>
    [Fact]
    public async Task Skip_being_written_does_not_undo_a_version_found_meanwhile()
    {
        var store = new GatedSkipStore();
        var controller = Create(store: store);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        var skipping = controller.SkipVersionAsync(TestContext.Current.CancellationToken);
        PublishRelease("v3.0.0");

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        store.Gate.SetResult();

        Assert.True(await skipping);
        Assert.Equal(SemanticVersion.Parse("3.0.0"), controller.Available?.Version);
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// Skipping while the user is being asked to install does nothing.
    /// </summary>
    [Fact]
    public async Task Skipping_while_the_user_is_being_asked_does_nothing()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource<bool>();
        _presenter.ConfirmGate = gate.Task;
        var installing = controller.InstallAsync(TestContext.Current.CancellationToken);

        var skipped = await controller.SkipVersionAsync(TestContext.Current.CancellationToken);
        gate.SetResult(false);
        await installing;

        Assert.False(skipped);
        Assert.Null(await _store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A wait longer than the clock can measure is refused when the controller is created.
    /// </summary>
    [Theory]
    [InlineData(60, 1)]
    [InlineData(0, 60)]
    public void Delays_the_clock_cannot_measure_are_refused(int initialDelayDays, int checkIntervalDays)
    {
        var updater = new ReleaseUpdater(Options(throttle: false, _store, NoChecksumProvider.Instance), _client);
        var options = new UpdateControllerOptions
        {
            InitialDelay = TimeSpan.FromDays(initialDelayDays),
            CheckInterval = TimeSpan.FromDays(checkIntervalDays),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateController(updater, _presenter, options));
    }

    /// <summary>
    /// Skipping records the version, forgets it and keeps automatic checks quiet about it.
    /// </summary>
    [Fact]
    public async Task Skipped_version_is_recorded_forgotten_and_not_announced_again()
    {
        var controller = Create();
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        var skipped = await controller.SkipVersionAsync(TestContext.Current.CancellationToken);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.True(skipped);
        Assert.Equal(SemanticVersion.Parse("2.0.0"), await _store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
        Assert.Null(controller.Available);
        Assert.Equal(UpdateState.UpToDate, controller.State);
        Assert.Single(_presenter.Announced);
    }

    /// <summary>
    /// Skipping with nothing found does nothing.
    /// </summary>
    [Fact]
    public async Task Skipping_with_nothing_found_does_nothing()
    {
        var controller = Create();

        Assert.False(await controller.SkipVersionAsync(TestContext.Current.CancellationToken));
        Assert.Null(await _store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A store that fails to record the skip is reported and the version stays available.
    /// </summary>
    [Fact]
    public async Task Failure_to_record_a_skip_is_reported_and_the_version_stays()
    {
        var controller = Create(store: new SkipFailingStore());
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        var skipped = await controller.SkipVersionAsync(TestContext.Current.CancellationToken);

        Assert.False(skipped);
        Assert.IsType<IOException>(Assert.Single(_errors));
        Assert.Equal(UpdateState.Available, controller.State);
    }

    /// <summary>
    /// A presenter that leaves the confirmation to its defaults never installs anything.
    /// </summary>
    [Fact]
    public async Task Presenter_with_default_confirmation_installs_nothing()
    {
        var controller = new UpdateController(
            new ReleaseUpdater(Options(throttle: false, _store, NoChecksumProvider.Instance), _client),
            new SilentPresenter(),
            new UpdateControllerOptions { DownloadDirectory = _directory });

        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        Assert.Empty(_launcher.Launched);
        Assert.Equal(UpdateState.Available, controller.State);
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
    /// A round of the loop deletes leftovers once the application is up to date, also when the checks are switched off.
    /// </summary>
    [Fact]
    public async Task Run_sweeps_leftovers_in_every_round_when_up_to_date()
    {
        PublishRelease("v1.0.0");
        var controller = Create(isAutoCheckEnabled: () => false, cleanDirectory: true);
        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.UpToDate, controller.State);
        Directory.CreateDirectory(_directory);
        var first = Path.Combine(_directory, "old-setup.exe");
        var second = Path.Combine(_directory, "older-setup.exe");
        await File.WriteAllBytesAsync(first, [1], TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => !File.Exists(first) && _time.PendingTimers == 1);
        await File.WriteAllBytesAsync(second, [1], TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => !File.Exists(second));

        Assert.Equal(0, _presenter.Confirmations);
        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// With asking turned off a manual check only reports what it found, and installing is left to the application.
    /// </summary>
    [Fact]
    public async Task Manual_check_does_not_ask_to_install_when_asking_is_off()
    {
        var controller = Create(askAfterManualCheck: false);

        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, controller.State);
        Assert.Equal(0, _presenter.Confirmations);
        Assert.Empty(_presenter.Announced);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Empty(_presenter.Announced);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _presenter.Confirmations);
        Assert.Single(_launcher.Launched);
    }

    /// <summary>
    /// A check that is throttled puts back the state it found, with the error that explains it.
    /// </summary>
    [Fact]
    public async Task Throttled_check_keeps_the_last_error_of_the_state_it_restores()
    {
        var failure = new IOException("held by a scanner");
        _launcher.Failure = failure;
        var controller = Create(throttle: true, store: new PlainStore());
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);
        Assert.Same(failure, controller.LastError);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);
        Assert.Same(failure, controller.LastError);
    }

    /// <summary>
    /// The last error is set with the state that follows a failure and cleared by the next change of state.
    /// </summary>
    [Fact]
    public async Task Last_error_follows_a_failure_and_ends_with_the_next_state()
    {
        var failure = new IOException("held by a scanner");
        _launcher.Failure = failure;
        Exception? seen = null;
        var controller = Create();
        controller.Changed += (_, _) => seen ??= controller.LastError;
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        Assert.Null(controller.LastError);

        await controller.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Same(failure, controller.LastError);
        Assert.Same(failure, seen);

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Null(controller.LastError);
    }

    /// <summary>
    /// A check that fails leaves its error in the last error, also when nobody is told.
    /// </summary>
    [Fact]
    public async Task Last_error_holds_the_error_of_a_failed_automatic_check()
    {
        var error = new HttpRequestException("offline");
        _client.ThrowOnFetch = error;
        var controller = Create();

        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Failed, controller.State);
        Assert.Same(error, controller.LastError);
        Assert.Empty(_presenter.Failures);
    }

    /// <summary>
    /// The progress of a download is kept and announced as it arrives.
    /// </summary>
    [Fact]
    public async Task Progress_is_kept_and_announced()
    {
        var controller = Create();
        var announced = 0;
        controller.ProgressChanged += (_, _) => Interlocked.Increment(ref announced);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        Assert.Null(controller.Progress);

        await controller.DownloadAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => controller.Progress is { BytesReceived: 10 });
        Assert.True(Volatile.Read(ref announced) >= 1);
    }

    /// <summary>
    /// With the option on, the rounds of the loop leave a known newer version alone.
    /// </summary>
    [Fact]
    public async Task Run_does_not_ask_again_while_a_newer_version_is_known_when_asked_not_to()
    {
        var controller = Create(askAfterManualCheck: false, skipWhileUpdateKnown: true);
        await controller.CheckAsync(manual: true, TestContext.Current.CancellationToken);
        PublishRelease("v3.0.0");
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        Assert.Equal(SemanticVersion.Parse("2.0.0"), controller.Available?.Version);
        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// When the check interval is not shorter than the minimum interval only the first round is throttled by the updater.
    /// </summary>
    [Fact]
    public async Task Run_is_not_throttled_by_a_minimum_interval_it_already_keeps()
    {
        var controller = Create(throttle: true, store: new AlwaysJustCheckedStore(_time));
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => _time.PendingTimers == 1);
        var afterFirst = _presenter.Announced.Count;
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => _presenter.Announced.Count == 1);

        Assert.Equal(0, afterFirst);
        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// A first round that made no check does not make the next one skip the throttling after a restart.
    /// </summary>
    [Fact]
    public async Task Run_treats_the_first_round_that_checks_as_the_first_one()
    {
        var enabled = false;
        var controller = Create(throttle: true, store: new AlwaysJustCheckedStore(_time), isAutoCheckEnabled: () => enabled);
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => _time.PendingTimers == 1);
        enabled = true;
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        Assert.Empty(_presenter.Announced);

        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => _presenter.Announced.Count == 1);
        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// A second run of the loop on the same controller is throttled in its first round like the first run was.
    /// </summary>
    [Fact]
    public async Task A_second_run_is_throttled_in_its_first_round()
    {
        var controller = Create(throttle: true, store: new AlwaysJustCheckedStore(_time));
        using var first = new CancellationTokenSource();
        var firstLoop = controller.RunAsync(first.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);
        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => _time.PendingTimers == 1);
        await first.CancelAsync();
        await firstLoop;

        using var second = new CancellationTokenSource();
        var secondLoop = controller.RunAsync(second.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);
        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        Assert.Empty(_presenter.Announced);

        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => _presenter.Announced.Count == 1);
        await second.CancelAsync();
        await secondLoop;
    }

    /// <summary>
    /// A progress report of an earlier download that arrives after the next one started is ignored.
    /// </summary>
    [Fact]
    public async Task Late_progress_of_an_earlier_download_is_ignored()
    {
        var controller = Create(launchInstaller: false);
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await controller.InstallAsync(TestContext.Current.CancellationToken);
        var earlier = controller.DownloadGeneration;
        File.Delete(controller.DownloadedFile!);
        await controller.InstallAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(earlier, controller.DownloadGeneration);
        var before = controller.Progress;

        controller.ApplyProgress(earlier, controller.Available!, new DownloadProgress(999, 1000, TimeSpan.FromSeconds(1)));

        Assert.Equal(before, controller.Progress);
        controller.ApplyProgress(controller.DownloadGeneration, controller.Available!, new DownloadProgress(999, 1000, TimeSpan.FromSeconds(1)));
        Assert.Equal(999, controller.Progress?.BytesReceived);
    }

    /// <summary>
    /// A round whose check already cleaned the directory does not sweep again; one without a check does.
    /// </summary>
    [Fact]
    public async Task Run_sweeps_only_in_rounds_whose_check_did_not_clean()
    {
        PublishRelease("v1.0.0");
        var enabled = true;
        var controller = Create(cleanDirectory: true, isAutoCheckEnabled: () => enabled);
        using var cts = new CancellationTokenSource();
        var loop = controller.RunAsync(cts.Token);
        await WaitUntilAsync(() => _time.PendingTimers == 1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => controller.State == UpdateState.UpToDate && _time.PendingTimers == 1);
        Assert.Equal(0, controller.Sweeps);

        enabled = false;
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => controller.Sweeps == 1);
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
    /// <param name="downloadRetries">Extra attempts the updater makes after a failed download.</param>
    /// <returns>The controller.</returns>
    private UpdateController Create(
        bool throttle = false,
        InstallerLaunchOptions? launchOptions = null,
        Func<bool>? isAutoCheckEnabled = null,
        bool requireVerified = false,
        IChecksumProvider? checksums = null,
        ILastCheckStore? store = null,
        int downloadRetries = 2,
        Func<CancellationToken, Task>? beforeInstall = null,
        bool watchInstaller = false,
        bool cleanDirectory = false,
        Func<int, Task>? waitForExit = null,
        TimeSpan? gracePeriod = null,
        TimeSpan? beforeInstallTimeout = null,
        bool launchInstaller = true,
        bool askAfterManualCheck = true,
        bool skipWhileUpdateKnown = false)
    {
        return new UpdateController(
            new ReleaseUpdater(Options(throttle, store ?? _store, checksums ?? NoChecksumProvider.Instance, downloadRetries), _client),
            _presenter,
            new UpdateControllerOptions
            {
                DownloadDirectory = _directory,
                InstallerLaunchOptions = launchOptions,
                IsAutoCheckEnabled = isAutoCheckEnabled,
                RequireVerifiedDownload = requireVerified,
                OnError = _errors.Add,
                TimeProvider = _time,
                BeforeInstallAsync = beforeInstall,
                WatchInstaller = watchInstaller,
                InstallerGracePeriod = gracePeriod ?? TimeSpan.Zero,
                BeforeInstallTimeout = beforeInstallTimeout ?? TimeSpan.FromSeconds(10),
                CleanDownloadDirectory = cleanDirectory,
                LaunchInstaller = launchInstaller,
                AskToInstallAfterManualCheck = askAfterManualCheck,
                SkipChecksWhileUpdateKnown = skipWhileUpdateKnown,
                WaitForProcessExitAsync = waitForExit is null ? (_, _) => Task.CompletedTask : (id, token) => waitForExit(id).WaitAsync(token),
            });
    }

    /// <summary>
    /// Makes the controller receive manual checks as soon as it starts its first check, as a user clicking "Check now"
    /// while a background check runs would.
    /// </summary>
    /// <param name="controller">The controller to watch.</param>
    /// <param name="count">How many manual checks to request.</param>
    /// <param name="token">The cancellation token every request is made with.</param>
    /// <param name="onRequested">Runs right after the requests were made; may be null.</param>
    /// <returns>The tasks of the requests, which the test can inspect once the first check has been made.</returns>
    private static List<Task> RequestManualChecksDuringFirstCheck(UpdateController controller, int count, CancellationToken token = default, Action? onRequested = null)
    {
        var calls = new List<Task>();
        var requested = false;
        controller.Changed += (_, _) =>
        {
            if (controller.State != UpdateState.Checking || requested)
            {
                return;
            }

            requested = true;
            for (var i = 0; i < count; i++)
            {
                calls.Add(controller.CheckAsync(manual: true, token));
            }

            onRequested?.Invoke();
        };
        return calls;
    }

    /// <summary>
    /// Builds the updater options used by the tests.
    /// </summary>
    /// <param name="throttle">Whether to throttle checks to once in 24 hours.</param>
    /// <param name="store">The last-check store.</param>
    /// <param name="checksums">The checksum provider.</param>
    /// <param name="downloadRetries">Extra attempts after a failed download.</param>
    /// <returns>The options.</returns>
    private UpdaterOptions Options(bool throttle, ILastCheckStore store, IChecksumProvider checksums, int downloadRetries = 2) => new()
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
        DownloadMaxRetryAttempts = downloadRetries,
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

        /// <summary>
        /// Gets or sets the process id the started installer reports.
        /// </summary>
        public int? ProcessId { get; set; }

        /// <inheritdoc />
        public InstallerLaunchResult Launch(string installerPath, InstallerLaunchOptions options)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Launched.Add((installerPath, options));
            return new InstallerLaunchResult(installerPath, ProcessId);
        }
    }

    /// <summary>
    /// A store that remembers the time of the last check and nothing else, so that it cannot lift the throttling.
    /// </summary>
    private sealed class PlainStore : ILastCheckStore
    {
        /// <summary>
        /// The time of the last check.
        /// </summary>
        private DateTimeOffset? _last;

        /// <inheritdoc />
        public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default) => Task.FromResult(_last);

        /// <inheritdoc />
        public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SemanticVersion?>(null);

        /// <inheritdoc />
        public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default)
        {
            _last = checkedAt;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A store that always says the last check was just made, so that every throttled check is refused.
    /// </summary>
    /// <param name="time">The clock.</param>
    private sealed class AlwaysJustCheckedStore(TimeProvider time) : ILastCheckStore
    {
        /// <inheritdoc />
        public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(time.GetUtcNow());

        /// <inheritdoc />
        public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SemanticVersion?>(null);

        /// <inheritdoc />
        public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
    /// A last-check store whose recording of a skipped version waits for the test to open <see cref="Gate"/>.
    /// </summary>
    private sealed class GatedSkipStore : ILastCheckStore
    {
        /// <summary>
        /// Gets the gate <see cref="SetSkippedVersionAsync"/> waits on.
        /// </summary>
        public TaskCompletionSource Gate { get; } = new();

        /// <inheritdoc />
        public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateTimeOffset?>(null);

        /// <inheritdoc />
        public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<SemanticVersion?>(null);

        /// <inheritdoc />
        public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default) => Gate.Task;

        /// <inheritdoc />
        public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A last-check store that records nothing and fails to record a skipped version.
    /// </summary>
    private sealed class SkipFailingStore : ILastCheckStore
    {
        /// <inheritdoc />
        public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateTimeOffset?>(null);

        /// <inheritdoc />
        public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<SemanticVersion?>(null);

        /// <inheritdoc />
        public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default) => throw new IOException("read-only");

        /// <inheritdoc />
        public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A presenter that implements nothing, so every member keeps its default.
    /// </summary>
    private sealed class SilentPresenter : IUpdatePresenter
    {
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
        /// Gets the files reported as downloaded and left for the user.
        /// </summary>
        public List<string> PackagesDownloaded { get; } = [];

        /// <summary>
        /// Gets how many times the application was told that the installer ended without replacing it.
        /// </summary>
        public int InstallerEndedCount { get; private set; }

        /// <summary>
        /// Gets how many times the application was told that the installer started.
        /// </summary>
        public int InstallerStartedCount { get; private set; }

        /// <summary>
        /// Gets the versions announced.
        /// </summary>
        public List<AvailableUpdate> Announced { get; } = [];

        /// <inheritdoc />
        public void InstallerEnded(AvailableUpdate update) => InstallerEndedCount++;

        /// <inheritdoc />
        public void PackageDownloaded(AvailableUpdate update, string filePath) => PackagesDownloaded.Add(filePath);

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

    /// <summary>
    /// A process that began before the installer was started only reuses its id; one that began at or after it is the installer.
    /// </summary>
    [Theory]
    [InlineData(-60, false)]
    [InlineData(-3, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(5, true)]
    public void Process_older_than_the_launch_is_not_the_installer(int secondsAfterLaunch, bool expected)
    {
        var launchedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, UpdateController.IsInstallerProcess(launchedAt.AddSeconds(secondsAfterLaunch), launchedAt));
    }

    /// <summary>
    /// Cleaning the download directory before a download keeps the files of the package under the name the downloader saves
    /// it as, which differs from the asset's own for a reserved Windows name.
    /// </summary>
    [Fact]
    public async Task Download_keeps_files_of_the_package_saved_under_a_sanitized_name()
    {
        _client.Latest = TestData.Release("v2.0.0", "setup:1.exe");
        _client.AssetBytes["setup:1.exe"] = new byte[10];
        Directory.CreateDirectory(_directory);
        var saved = AssetDownloader.SanitizeFileName("setup:1.exe");
        var kept = Path.Combine(_directory, saved + ".partial.note");
        var stray = Path.Combine(_directory, "old-setup.exe");
        await File.WriteAllBytesAsync(kept, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(stray, [1], TestContext.Current.CancellationToken);
        var options = new UpdaterOptions
        {
            Owner = "o",
            Repo = "r",
            CurrentVersion = "1.0.0",
            AssetSelector = new DelegateAssetSelector(a => a.Name == "setup:1.exe"),
            ChecksumProvider = NoChecksumProvider.Instance,
            InstallerLauncher = _launcher,
            LastCheckStore = _store,
            TimeProvider = _time,
        };
        var controller = new UpdateController(new ReleaseUpdater(options, _client), _presenter, new UpdateControllerOptions
        {
            DownloadDirectory = _directory,
            CleanDownloadDirectory = true,
            OnError = _errors.Add,
            TimeProvider = _time,
        });
        await controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        await controller.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Downloaded, controller.State);
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(stray));
        Assert.Equal(Path.Combine(_directory, saved), controller.DownloadedFile);
    }
}
