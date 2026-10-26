namespace GitHubReleaseUpdater.AutoUpdate;

/// <summary>
/// What an <see cref="UpdateController"/> is doing or found last.
/// </summary>
public enum UpdateState
{
    /// <summary>
    /// No check has completed yet.
    /// </summary>
    Idle,

    /// <summary>
    /// A check is running.
    /// </summary>
    Checking,

    /// <summary>
    /// The last check found nothing newer, or only a version the user chose to skip.
    /// </summary>
    UpToDate,

    /// <summary>
    /// A newer version was found and waits to be installed.
    /// </summary>
    Available,

    /// <summary>
    /// The last check could not be made and no newer version is known.
    /// </summary>
    Failed,

    /// <summary>
    /// A version is being downloaded and its installer started.
    /// </summary>
    Installing,

    /// <summary>
    /// The installer of the available version is being downloaded by <see cref="UpdateController.DownloadAsync"/>.
    /// </summary>
    Downloading,

    /// <summary>
    /// The package was downloaded and verified and waits to be started, or, when
    /// <see cref="UpdateControllerOptions.LaunchInstaller"/> is off, to be used by the user (for example an archive to unpack).
    /// </summary>
    Downloaded,
}
