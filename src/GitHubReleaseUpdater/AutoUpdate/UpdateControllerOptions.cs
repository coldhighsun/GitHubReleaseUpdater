using GitHubReleaseUpdater.Installation;

namespace GitHubReleaseUpdater.AutoUpdate;

/// <summary>
/// Settings of an <see cref="UpdateController"/>.
/// </summary>
public sealed class UpdateControllerOptions
{
    /// <summary>
    /// How long <see cref="UpdateController.RunAsync"/> waits before the first automatic check, so it does not compete
    /// with the application starting up. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="UpdateController.RunAsync"/> waits between automatic checks. Defaults to 24 hours.
    /// </summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Asked before every automatic check; a round in which it returns false is skipped. Lets the application honour a
    /// "check for updates automatically" setting that can change while it runs. Null means always check.
    /// </summary>
    public Func<bool>? IsAutoCheckEnabled { get; init; }

    /// <summary>
    /// The directory the installer is downloaded to. Defaults to a folder named after the repository under the system
    /// temporary directory, which on some systems is shared with other users; set this to a folder only the current user
    /// can write to if that matters. The installer is left there after it was started, since it may still be running.
    /// </summary>
    public string? DownloadDirectory { get; init; }

    /// <summary>
    /// Arguments and options for starting the installer, for example <see cref="InstallerLaunchOptions.InnoSetupSilent"/>.
    /// Null starts it without arguments.
    /// </summary>
    public InstallerLaunchOptions? InstallerLaunchOptions { get; init; }

    /// <summary>
    /// When true (the default), an installer is only started if its checksum was verified against the one published
    /// with the release (a sidecar file, a sums file or the digest GitHub reports); otherwise the installation fails
    /// and is reported to <see cref="IUpdatePresenter.ReportInstallFailed"/>. Applies on top of
    /// <see cref="InstallerLaunchOptions"/>, whose presets do not require it. Set to false only for releases that
    /// cannot be verified, such as some GitHub Enterprise Server versions, and you accept running an unchecked download.
    /// </summary>
    public bool RequireVerifiedDownload { get; init; } = true;

    /// <summary>
    /// The clock the waits of <see cref="UpdateController.RunAsync"/> are measured on. Defaults to
    /// <see cref="System.TimeProvider.System"/>; tests can pass a fake one.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Receives an exception that would otherwise have nowhere to go: one thrown by the presenter, by a handler of
    /// <see cref="UpdateController.Changed"/> or by <see cref="IsAutoCheckEnabled"/>, or the failure to record the time of a
    /// check in the last-check store. The controller carries on after each of them. Null ignores them.
    /// </summary>
    public Action<Exception>? OnError { get; init; }

    /// <summary>
    /// Runs just before the installer is started, for example to save what is still waiting to be saved: an installer
    /// usually ends the application without its own shutdown. A failure of it (reported to <see cref="OnError"/>) or
    /// exceeding <see cref="BeforeInstallTimeout"/> does not stop the installation. Null does nothing.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeInstallAsync { get; init; }

    /// <summary>
    /// How long <see cref="BeforeInstallAsync"/> is given. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan BeforeInstallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// When true, <see cref="UpdateController.InstallAsync"/> stays in <see cref="UpdateState.Installing"/> until the
    /// installer process has ended and <see cref="InstallerGracePeriod"/> has passed. If the application is still running
    /// by then, the installer did not replace it (the user cancelled, or it failed), the state goes back to
    /// <see cref="UpdateState.Downloaded"/> and <see cref="IUpdatePresenter.InstallerEnded"/> is called. When false (the
    /// default), the call returns as soon as the installer is started.
    /// </summary>
    public bool WatchInstaller { get; init; }

    /// <summary>
    /// How long to wait after the installer process ended before concluding that it did not replace the application,
    /// since an installer may hand over to an elevated copy of itself and end first. Defaults to 30 seconds. An
    /// installer that cannot be watched is given ten times as long.
    /// </summary>
    public TimeSpan InstallerGracePeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, files of earlier downloads are deleted from <see cref="DownloadDirectory"/> before a new download
    /// (except the partial file of the package about to be fetched, which lets an interrupted download go on) and when a
    /// check finds the application up to date. A file that cannot be deleted, such as an installer still finishing,
    /// stays for the next time. Only enable this for a directory used for nothing else. Default false.
    /// </summary>
    public bool CleanDownloadDirectory { get; init; }

    /// <summary>
    /// Waits for a process to end; replaceable so that tests need no real process. Defaults to watching the process.
    /// </summary>
    internal Func<int, CancellationToken, Task>? WaitForProcessExitAsync { get; init; }

    /// <summary>
    /// When false, <see cref="UpdateController.InstallAsync"/> only downloads and verifies the package and stops in
    /// <see cref="UpdateState.Downloaded"/>, without starting it, for packages that are not installers, such as an archive
    /// the user unpacks. <see cref="UpdateController.DownloadedFile"/> then names the file. Defaults to true.
    /// </summary>
    public bool LaunchInstaller { get; init; } = true;
}
