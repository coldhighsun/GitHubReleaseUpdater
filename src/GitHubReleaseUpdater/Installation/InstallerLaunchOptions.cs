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

    /// <summary>
    /// Returns a copy of these options with extra arguments appended, for example the installer's own switches to close
    /// and restart the application: <c>InnoSetupSilent.WithArguments("/CLOSEAPPLICATIONS /RELAUNCH=1")</c>.
    /// </summary>
    /// <param name="extraArguments">The arguments to append, separated from the existing ones by a space.</param>
    /// <returns>The new options; this instance is not changed.</returns>
    public InstallerLaunchOptions WithArguments(string extraArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extraArguments);
        return new InstallerLaunchOptions
        {
            Arguments = string.IsNullOrWhiteSpace(Arguments) ? extraArguments : Arguments + " " + extraArguments,
            WorkingDirectory = WorkingDirectory,
            RequireVerified = RequireVerified,
        };
    }
}
