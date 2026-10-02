namespace GitHubReleaseUpdater.Exceptions;

/// <summary>
/// Raised when a downloaded installer cannot be started, including when the user declines its elevation prompt.
/// </summary>
public sealed class InstallerLaunchException : UpdaterException
{
    /// <summary>
    /// Full path of the installer that could not be started.
    /// </summary>
    public string? FilePath { get; }

    /// <summary>
    /// True when the user declined the elevation (UAC) prompt, as opposed to a genuine launch failure.
    /// </summary>
    public bool IsUserCancelled { get; }

    /// <inheritdoc />
    public InstallerLaunchException() { }
    /// <inheritdoc />
    public InstallerLaunchException(string message) : base(message) { }
    /// <inheritdoc />
    public InstallerLaunchException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>
    /// Creates a new instance for the installer at <paramref name="filePath"/>.
    /// </summary>
    public InstallerLaunchException(string message, string? filePath, bool isUserCancelled = false, Exception? innerException = null)
        : base(message, innerException)
    {
        FilePath = filePath;
        IsUserCancelled = isUserCancelled;
    }
}
