namespace GitHubReleaseUpdater.AutoUpdate;

/// <summary>
/// The part of the update flow in which a failure happened.
/// </summary>
public enum UpdateFailureStage
{
    /// <summary>
    /// Asking GitHub for the latest release.
    /// </summary>
    Check,

    /// <summary>
    /// Downloading and verifying the package, including the check of the installer by the
    /// <see cref="UpdaterOptions.InstallerVerifier"/> right after the download.
    /// </summary>
    Download,

    /// <summary>
    /// Preparing for and starting the installer, including its verification just before it starts.
    /// </summary>
    Install,
}

/// <summary>
/// Why the last check, download or installation of an <see cref="UpdateController"/> failed.
/// </summary>
/// <param name="Stage">The part of the flow that failed.</param>
/// <param name="Error">The exception.</param>
/// <param name="UserInitiated">
/// True if the user asked for what failed: a manual check, or a download or installation started by the application on
/// their behalf. False for an automatic check, whose failure an application usually does not show.
/// </param>
public sealed record UpdateFailure(UpdateFailureStage Stage, Exception Error, bool UserInitiated);
