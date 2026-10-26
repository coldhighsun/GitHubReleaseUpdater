using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater.AutoUpdate;

/// <summary>
/// Everything an <see cref="UpdateController"/> needs from the application: telling the user things, asking them,
/// and acting once the installer has started. Every member has a default that does nothing (and agrees to install),
/// so an implementation overrides only what it needs. Members are called on the context that called the controller,
/// normally the UI thread.
/// </summary>
public interface IUpdatePresenter
{
    /// <summary>
    /// Tells the user, without interrupting them, that a newer version was found by an automatic check. Called once per
    /// version.
    /// </summary>
    /// <param name="update">The newer release.</param>
    void Announce(AvailableUpdate update)
    {
    }

    /// <summary>
    /// Tells the user that a check they asked for found nothing newer.
    /// </summary>
    void ReportUpToDate()
    {
    }

    /// <summary>
    /// Tells the user that a check they asked for could not be made.
    /// </summary>
    /// <param name="error">Why, or null if the check ended without an error (for example a release without an installer).</param>
    void ReportCheckFailed(Exception? error)
    {
    }

    /// <summary>
    /// Asks whether to download and install a version now. The default answers no, so a presenter that does not ask
    /// the user never installs anything on its own.
    /// </summary>
    /// <param name="update">The release to install.</param>
    /// <param name="currentVersion">The running version.</param>
    /// <returns>True if the user agrees.</returns>
    ValueTask<bool> ConfirmInstallAsync(AvailableUpdate update, SemanticVersion currentVersion)
    {
        return ValueTask.FromResult(false);
    }

    /// <summary>
    /// Tells the user that the download has begun.
    /// </summary>
    /// <param name="update">The release being downloaded.</param>
    void ReportDownloading(AvailableUpdate update)
    {
    }

    /// <summary>
    /// Reports how far the download has come. Called on the context that started the installation.
    /// </summary>
    /// <param name="update">The release being downloaded.</param>
    /// <param name="progress">The state of the download.</param>
    void ReportProgress(AvailableUpdate update, DownloadProgress progress)
    {
    }

    /// <summary>
    /// Tells the user that the update could not be installed. Not called when the user declined an elevation prompt.
    /// </summary>
    /// <param name="update">The release that failed to install.</param>
    /// <param name="error">Why.</param>
    void ReportInstallFailed(AvailableUpdate update, Exception error)
    {
    }

    /// <summary>
    /// Called after the installer has been started. The application is expected to close so the installer can replace
    /// its files.
    /// </summary>
    /// <param name="update">The release being installed.</param>
    void InstallerStarted(AvailableUpdate update)
    {
    }
}
