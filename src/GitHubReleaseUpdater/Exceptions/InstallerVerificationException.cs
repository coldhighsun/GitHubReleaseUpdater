namespace GitHubReleaseUpdater.Exceptions;

/// <summary>
/// Raised when an installer did not pass the <see cref="Verification.IInstallerVerifier"/> that was asked just before it
/// would have been started. The installer is not started.
/// </summary>
public sealed class InstallerVerificationException : UpdaterException
{
    /// <summary>
    /// Gets the full path of the installer that was refused.
    /// </summary>
    public string? FilePath { get; }

    /// <summary>
    /// Gets why the verifier refused it.
    /// </summary>
    public string? Reason { get; }

    /// <inheritdoc />
    public InstallerVerificationException() { }

    /// <inheritdoc />
    public InstallerVerificationException(string message) : base(message) { }

    /// <inheritdoc />
    public InstallerVerificationException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>
    /// Creates the exception for an installer the verifier refused.
    /// </summary>
    /// <param name="filePath">The installer.</param>
    /// <param name="reason">Why it was refused.</param>
    internal InstallerVerificationException(string filePath, string reason)
        : base($"The installer '{filePath}' failed verification and was not started: {reason}")
    {
        FilePath = filePath;
        Reason = reason;
    }
}
