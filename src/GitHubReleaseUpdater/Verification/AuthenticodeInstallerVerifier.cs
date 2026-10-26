using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GitHubReleaseUpdater.Verification;

/// <summary>
/// Checks the Authenticode signature of a Windows installer (<c>.exe</c> or <c>.msi</c>) with the operating system, and
/// optionally who signed it. On other systems every installer is refused, since nothing can be checked: set the
/// verifier only where <see cref="IsSupported"/> is true.
/// </summary>
/// <remarks>
/// The signature must be embedded in the file and chain to a root the machine trusts. Revocation is not checked online, so
/// that a machine without network does not refuse every installer. Without <see cref="ExpectedSubjects"/>,
/// <see cref="ExpectedThumbprints"/> or <see cref="ExpectedPublishers"/> any valid signature is accepted, which only
/// shows that the file was signed by someone; name the signer you expect (preferably with
/// <see cref="ExpectedSubjects"/>) to rule out a validly signed installer of someone else. When several kinds are given,
/// the certificate must satisfy each of them.
/// </remarks>
public sealed partial class AuthenticodeInstallerVerifier : IInstallerVerifier
{
    /// <summary>
    /// Gets a value indicating whether this system can check Authenticode signatures.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Gets the certificate thumbprints the signing certificate must have one of, as hex text (spaces and colons are
    /// ignored; 40 digits for SHA-1, 64 for SHA-256). Empty accepts any.
    /// </summary>
    public IReadOnlyCollection<string> ExpectedThumbprints { get; init; } = [];

    /// <summary>
    /// Gets the full subject names (distinguished names such as <c>CN=Contoso Ltd, O=Contoso Ltd, L=Redmond, C=US</c>)
    /// the signing certificate must have one of, compared as names (the order of the parts counts, case does not). This is
    /// what pins a publisher: the common name alone is not unique, since another company can hold a certificate with the
    /// same one. Unlike a thumbprint it survives the renewal of the certificate. Empty accepts any.
    /// </summary>
    public IReadOnlyCollection<string> ExpectedSubjects { get; init; } = [];

    /// <summary>
    /// Gets the names the signing certificate must show as its simple name (the common name of its subject, such as
    /// <c>Contoso Ltd</c>), one of which must match, ignoring case. Empty accepts any.
    /// </summary>
    public IReadOnlyCollection<string> ExpectedPublishers { get; init; } = [];

    /// <inheritdoc />
    public Task<InstallerVerificationResult> VerifyAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (!IsSupported)
        {
            return Task.FromResult(InstallerVerificationResult.Invalid("Authenticode signatures can only be checked on Windows."));
        }

        // The system call may block on the file system and on certificate chains.
        return Task.Run(() => Verify(installerPath), cancellationToken);
    }

    /// <summary>
    /// Checks one file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The outcome.</returns>
    private InstallerVerificationResult Verify(string path)
    {
        var status = NativeMethods.VerifyEmbeddedSignature(path);
        if (status != 0)
        {
            return InstallerVerificationResult.Invalid(DescribeTrustStatus(status));
        }

        X509Certificate2 signer;
        try
        {
#pragma warning disable SYSLIB0057 // The loader for files does not read the signer of a signed file.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            signer = new X509Certificate2(certificate);
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException ex)
        {
            return InstallerVerificationResult.Invalid($"The signer could not be read: {ex.Message}");
        }

        using (signer)
        {
            var publisher = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (ExpectedSubjects.Count > 0)
            {
                bool matched;
                try
                {
                    matched = ExpectedSubjects.Any(expected => string.Equals(
                        new X500DistinguishedName(expected).Name,
                        signer.SubjectName.Name,
                        StringComparison.OrdinalIgnoreCase));
                }
                catch (CryptographicException)
                {
                    return InstallerVerificationResult.Invalid("ExpectedSubjects holds a name that is not a valid distinguished name.", publisher);
                }

                if (!matched)
                {
                    return InstallerVerificationResult.Invalid($"The installer is signed by '{signer.Subject}', which is not an expected subject.", publisher);
                }
            }

            if (ExpectedPublishers.Count > 0 && !ExpectedPublishers.Contains(publisher, StringComparer.OrdinalIgnoreCase))
            {
                return InstallerVerificationResult.Invalid($"The installer is signed by '{publisher}', which is not an expected publisher.", publisher);
            }

            if (ExpectedThumbprints.Count > 0)
            {
                var sha1 = signer.Thumbprint;
                var sha256 = signer.GetCertHashString(HashAlgorithmName.SHA256);
                var matches = ExpectedThumbprints.Select(NormalizeThumbprint).Any(t => string.Equals(t, sha1, StringComparison.OrdinalIgnoreCase)
                                                                                        || string.Equals(t, sha256, StringComparison.OrdinalIgnoreCase));
                if (!matches)
                {
                    return InstallerVerificationResult.Invalid($"The signing certificate of the installer ({sha1}) is not an expected one.", publisher);
                }
            }

            return InstallerVerificationResult.Valid(publisher);
        }
    }

    /// <summary>
    /// Removes the separators people put into thumbprints.
    /// </summary>
    /// <param name="thumbprint">The thumbprint as written.</param>
    /// <returns>The hex digits only.</returns>
    private static string NormalizeThumbprint(string thumbprint) =>
        new(thumbprint.Where(Uri.IsHexDigit).ToArray());

    /// <summary>
    /// Names the result of the trust check.
    /// </summary>
    /// <param name="status">The result of <c>WinVerifyTrust</c>.</param>
    /// <returns>A sentence for the user.</returns>
    private static string DescribeTrustStatus(int status) => unchecked((uint)status) switch
    {
        0x800B0100 => "The installer is not signed.",
        0x800B0003 => "The installer is not a file that can carry a signature.",
        0x800B0101 => "The certificate of the installer has expired.",
        0x800B0109 => "The certificate chain of the installer ends in a root that is not trusted.",
        0x800B010A => "The certificate chain of the installer could not be built.",
        0x80096010 => "The signature of the installer does not match its content.",
        0x800B0111 => "The installer is signed with a certificate that was explicitly distrusted.",
        _ => $"The signature of the installer is not valid (0x{unchecked((uint)status):X8}).",
    };

    /// <summary>
    /// The call into the operating system.
    /// </summary>
    private static partial class NativeMethods
    {
        /// <summary>
        /// <c>WTD_UI_NONE</c>: no dialog.
        /// </summary>
        private const uint UiNone = 2;

        /// <summary>
        /// <c>WTD_REVOKE_NONE</c>: no revocation check.
        /// </summary>
        private const uint RevokeNone = 0;

        /// <summary>
        /// <c>WTD_CHOICE_FILE</c>: the data is a file.
        /// </summary>
        private const uint ChoiceFile = 1;

        /// <summary>
        /// <c>WTD_STATEACTION_VERIFY</c>.
        /// </summary>
        private const uint StateVerify = 1;

        /// <summary>
        /// <c>WTD_STATEACTION_CLOSE</c>.
        /// </summary>
        private const uint StateClose = 2;

        /// <summary>
        /// <c>WTD_REVOCATION_CHECK_NONE</c>.
        /// </summary>
        private const uint ProviderFlagsNoRevocation = 0x10;

        /// <summary>
        /// <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>.
        /// </summary>
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <summary>
        /// Asks Windows whether the file carries an embedded signature that it trusts.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <returns>0 if it does, otherwise the <c>HRESULT</c> that says why not.</returns>
        public static int VerifyEmbeddedSignature(string path)
        {
            var pathPointer = IntPtr.Zero;
            var infoPointer = IntPtr.Zero;
            var started = false;
            var data = default(WintrustData);
            var action = GenericVerifyV2;
            try
            {
                pathPointer = Marshal.StringToHGlobalUni(path);
                infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
                Marshal.StructureToPtr(
                    new WintrustFileInfo { CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), FilePath = pathPointer },
                    infoPointer,
                    fDeleteOld: false);
                data = new WintrustData
                {
                    CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                    UiChoice = UiNone,
                    RevocationChecks = RevokeNone,
                    UnionChoice = ChoiceFile,
                    File = infoPointer,
                    StateAction = StateVerify,
                    ProviderFlags = ProviderFlagsNoRevocation,
                };
                started = true;
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            finally
            {
                if (started)
                {
                    data.StateAction = StateClose;
                    _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                }

                if (infoPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(infoPointer);
                }

                if (pathPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pathPointer);
                }
            }
        }

        /// <summary>
        /// <c>WinVerifyTrust</c> of <c>wintrust.dll</c>.
        /// </summary>
        /// <param name="window">The window for dialogs; -1 for none.</param>
        /// <param name="action">The trust provider action.</param>
        /// <param name="data">What to check.</param>
        /// <returns>0 for trusted, otherwise an <c>HRESULT</c>.</returns>
        [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
        private static partial int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);

        /// <summary>
        /// <c>WINTRUST_FILE_INFO</c>.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustFileInfo
        {
            public uint CbStruct;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        /// <summary>
        /// <c>WINTRUST_DATA</c>.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint CbStruct;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }
    }
}
