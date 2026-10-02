namespace GitHubReleaseUpdater.Installation;

/// <summary>
/// How a downloaded installer is started by <see cref="IInstallerLauncher"/>.
/// </summary>
public sealed class InstallerLaunchOptions
{
    /// <summary>
    /// Command-line arguments passed to the installer, or null to start it without any.
    /// </summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// Working directory of the installer process. Null uses the directory containing the installer.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// When true, <see cref="Installation.IInstallerLauncher"/> is only invoked for a download whose checksum was
    /// actually verified; an unverified download fails with <see cref="Exceptions.UpdaterException"/> instead of
    /// being run. Default false.
    /// </summary>
    public bool RequireVerified { get; init; }

    /// <summary>
    /// Inno Setup <c>/SILENT</c>: no wizard pages, but a progress window is shown. Does not ask for a restart.
    /// </summary>
    public static InstallerLaunchOptions InnoSetupSilent { get; } = new() { Arguments = "/SILENT /SP- /NORESTART" };

    /// <summary>
    /// Inno Setup <c>/VERYSILENT</c>: no wizard pages and no progress window. Does not ask for a restart.
    /// </summary>
    public static InstallerLaunchOptions InnoSetupVerySilent { get; } = new() { Arguments = "/VERYSILENT /SP- /NORESTART" };
}
