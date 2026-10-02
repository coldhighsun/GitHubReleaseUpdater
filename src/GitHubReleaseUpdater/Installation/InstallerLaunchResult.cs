namespace GitHubReleaseUpdater.Installation;

/// <summary>
/// Outcome of a successful <see cref="IInstallerLauncher.Launch"/>.
/// </summary>
/// <param name="InstallerPath">Full path of the installer that was started.</param>
/// <param name="ProcessId">Id of the started process, or null when the launcher did not start a new process.</param>
public sealed record InstallerLaunchResult(string InstallerPath, int? ProcessId);
