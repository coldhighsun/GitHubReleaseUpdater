namespace GitHubReleaseUpdater.Verification;

/// <summary>
/// An extra check of a downloaded installer, made just before it is started (and by the update controller right after the
/// download as well, even when nothing is started), on top of the SHA-256 checksum: the checksum
/// comes from the same release, so whoever can change the release can change both. A signature made with a key that only
/// the publisher holds closes that gap. Set it with <see cref="UpdaterOptions.InstallerVerifier"/>.
/// </summary>
public interface IInstallerVerifier
{
    /// <summary>
    /// Checks the installer.
    /// </summary>
    /// <param name="installerPath">The full path of the installer.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether the installer may be started, and why not if it may not. Never null: a null task or result is treated as a refusal, not as approval.</returns>
    Task<InstallerVerificationResult> VerifyAsync(string installerPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of an <see cref="IInstallerVerifier"/>.
/// </summary>
/// <param name="IsValid">True if the installer may be started.</param>
/// <param name="Reason">Why it may not, or null.</param>
/// <param name="Signer">Who signed it, if the verifier could tell; for diagnostics.</param>
public sealed record InstallerVerificationResult(bool IsValid, string? Reason = null, string? Signer = null)
{
    /// <summary>
    /// Gets the result for an installer that may be started.
    /// </summary>
    /// <param name="signer">Who signed it, if known.</param>
    /// <returns>The result.</returns>
    public static InstallerVerificationResult Valid(string? signer = null) => new(true, null, signer);

    /// <summary>
    /// Gets the result for an installer that may not be started.
    /// </summary>
    /// <param name="reason">Why not.</param>
    /// <param name="signer">Who signed it, if known.</param>
    /// <returns>The result.</returns>
    public static InstallerVerificationResult Invalid(string reason, string? signer = null) => new(false, reason, signer);
}
