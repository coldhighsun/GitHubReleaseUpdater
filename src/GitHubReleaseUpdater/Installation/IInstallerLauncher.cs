namespace GitHubReleaseUpdater.Installation;

/// <summary>
/// Starts a downloaded installer. Implement this to customize how (or whether) the installer is run, e.g. in tests.
/// </summary>
public interface IInstallerLauncher
{
    /// <summary>
    /// Starts the installer at <paramref name="installerPath"/> without waiting for it to finish.
    /// </summary>
    /// <exception cref="Exceptions.InstallerLaunchException">The installer could not be started.</exception>
    InstallerLaunchResult Launch(string installerPath, InstallerLaunchOptions options);
}
