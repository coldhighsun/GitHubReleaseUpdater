namespace GitHubReleaseUpdater.Exceptions;

/// <summary>Base class for all exceptions raised by this library.</summary>
public class UpdaterException : Exception
{
    /// <inheritdoc />
    public UpdaterException() { }
    /// <inheritdoc />
    public UpdaterException(string message) : base(message) { }
    /// <inheritdoc />
    public UpdaterException(string message, Exception? innerException) : base(message, innerException) { }
}
