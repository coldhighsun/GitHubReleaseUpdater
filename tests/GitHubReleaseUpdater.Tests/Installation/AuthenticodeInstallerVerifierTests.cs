using GitHubReleaseUpdater.Verification;

namespace GitHubReleaseUpdater.Tests;

/// <summary>
/// Tests for <see cref="AuthenticodeInstallerVerifier"/>. The positive cases use <c>dotnet.exe</c> of the installed SDK,
/// which Microsoft signs with an embedded signature, so they run on Windows with the SDK that builds the tests.
/// </summary>
public sealed class AuthenticodeInstallerVerifierTests : IDisposable
{
    /// <summary>
    /// A directory for the files of one test.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ghru-auth-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Finds the signed <c>dotnet.exe</c> that runs the tests.
    /// </summary>
    /// <returns>The path, or null if there is none.</returns>
    private static string? SignedFile()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        var path = Path.Combine(root, "dotnet.exe");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public async Task Other_systems_refuse_everything()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows can check signatures.");

        var result = await new AuthenticodeInstallerVerifier().VerifyAsync(Path.Combine(_dir, "setup.exe"), TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.False(AuthenticodeInstallerVerifier.IsSupported);
    }

    [Fact]
    public async Task An_unsigned_file_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "setup.exe");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        var result = await new AuthenticodeInstallerVerifier().VerifyAsync(path, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task A_signed_file_is_accepted_when_nothing_is_expected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");

        var result = await new AuthenticodeInstallerVerifier().VerifyAsync(file!, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, result.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.Signer));
    }

    [Fact]
    public async Task A_signed_file_is_accepted_for_its_expected_publisher()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");

        var own = await new AuthenticodeInstallerVerifier().VerifyAsync(file!, TestContext.Current.CancellationToken);

        var result = await new AuthenticodeInstallerVerifier { ExpectedPublishers = [own.Signer!.ToUpperInvariant()] }.VerifyAsync(file!, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public async Task A_signed_file_is_refused_for_another_publisher()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");

        var result = await new AuthenticodeInstallerVerifier { ExpectedPublishers = ["Contoso Ltd"] }.VerifyAsync(file!, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Signer));
    }

    [Fact]
    public async Task A_signed_file_is_refused_for_another_thumbprint_and_accepted_for_its_own()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");
        var own = await new AuthenticodeInstallerVerifier().VerifyAsync(file!, TestContext.Current.CancellationToken);
        Assert.True(own.IsValid, own.Reason);
#pragma warning disable SYSLIB0057 // There is no other way to read the signer of a signed file.
        using var signer = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file!);
#pragma warning restore SYSLIB0057
        var thumbprint = signer.GetCertHashString();

        var refused = await new AuthenticodeInstallerVerifier { ExpectedThumbprints = [new string('0', 40)] }.VerifyAsync(file!, TestContext.Current.CancellationToken);
        var accepted = await new AuthenticodeInstallerVerifier { ExpectedThumbprints = [thumbprint.ToLowerInvariant()] }.VerifyAsync(file!, TestContext.Current.CancellationToken);

        Assert.False(refused.IsValid);
        Assert.True(accepted.IsValid, accepted.Reason);
    }

    [Fact]
    public async Task A_tampered_signed_file_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");
        Directory.CreateDirectory(_dir);
        var copy = Path.Combine(_dir, "dotnet.exe");
        File.Copy(file!, copy);
        await using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 1024;
            var b = stream.ReadByte();
            stream.Position = 1024;
            stream.WriteByte((byte)(b ^ 0xFF));
        }

        var result = await new AuthenticodeInstallerVerifier().VerifyAsync(copy, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
    }

    /// <summary>
    /// Gets the signer details of the signed file, from what the verifier reports and from the file itself.
    /// </summary>
    /// <param name="file">The signed file.</param>
    /// <returns>The full subject, the SHA-1 thumbprint and the SHA-256 thumbprint of the signing certificate.</returns>
    private static (string Subject, string Sha1, string Sha256) SignerOf(string file)
    {
#pragma warning disable SYSLIB0057 // There is no other way to read the signer of a signed file.
        using var signer = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
        using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(signer);
        return (certificate.Subject, certificate.Thumbprint, certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task The_full_subject_is_accepted_and_another_one_with_the_same_common_name_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");
        var (subject, _, _) = SignerOf(file!);
        var own = await new AuthenticodeInstallerVerifier().VerifyAsync(file!, TestContext.Current.CancellationToken);
        var impostor = $"CN={own.Signer}, O=Someone Else, C=XX";

        var accepted = await new AuthenticodeInstallerVerifier { ExpectedSubjects = [subject.ToUpperInvariant()] }.VerifyAsync(file!, TestContext.Current.CancellationToken);
        var refused = await new AuthenticodeInstallerVerifier { ExpectedSubjects = [impostor] }.VerifyAsync(file!, TestContext.Current.CancellationToken);
        var commonNameOnly = await new AuthenticodeInstallerVerifier { ExpectedPublishers = [own.Signer!] }.VerifyAsync(file!, TestContext.Current.CancellationToken);

        Assert.True(accepted.IsValid, accepted.Reason);
        Assert.False(refused.IsValid);
        Assert.True(commonNameOnly.IsValid, "the simple name alone still matches, which is why subjects exist");
    }

    [Fact]
    public async Task An_invalid_expected_subject_is_refused_not_ignored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");

        var result = await new AuthenticodeInstallerVerifier { ExpectedSubjects = ["this is not a name"] }.VerifyAsync(file!, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains("distinguished name", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_kind_of_expectation_that_is_given_must_match()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");
        var own = await new AuthenticodeInstallerVerifier().VerifyAsync(file!, TestContext.Current.CancellationToken);
        var (_, sha1, _) = SignerOf(file!);

        var publisherRightThumbprintWrong = await new AuthenticodeInstallerVerifier { ExpectedPublishers = [own.Signer!], ExpectedThumbprints = [new string('0', 40)] }.VerifyAsync(file!, TestContext.Current.CancellationToken);
        var publisherWrongThumbprintRight = await new AuthenticodeInstallerVerifier { ExpectedPublishers = ["Contoso Ltd"], ExpectedThumbprints = [sha1] }.VerifyAsync(file!, TestContext.Current.CancellationToken);
        var bothRight = await new AuthenticodeInstallerVerifier { ExpectedPublishers = [own.Signer!], ExpectedThumbprints = [sha1] }.VerifyAsync(file!, TestContext.Current.CancellationToken);

        Assert.False(publisherRightThumbprintWrong.IsValid);
        Assert.False(publisherWrongThumbprintRight.IsValid);
        Assert.True(bothRight.IsValid, bothRight.Reason);
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("sha1-colons")]
    [InlineData("sha1-spaces")]
    public async Task Thumbprints_are_accepted_in_the_forms_people_write_them(string form)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var file = SignedFile();
        Assert.SkipWhen(file is null, "No signed dotnet.exe found.");
        var (_, sha1, sha256) = SignerOf(file!);
        var thumbprint = form switch
        {
            "sha256" => sha256,
            "sha1-colons" => string.Join(':', Enumerable.Range(0, sha1.Length / 2).Select(i => sha1.Substring(i * 2, 2))),
            _ => string.Join(' ', Enumerable.Range(0, sha1.Length / 4).Select(i => sha1.Substring(i * 4, 4))).ToLowerInvariant(),
        };

        var result = await new AuthenticodeInstallerVerifier { ExpectedThumbprints = [thumbprint] }.VerifyAsync(file!, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public async Task A_signed_msi_is_accepted_and_its_signer_is_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is checked on Windows only.");
        var windowsInstaller = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer");
        var candidates = Directory.Exists(windowsInstaller) ? Directory.EnumerateFiles(windowsInstaller, "*.msi").Order(StringComparer.Ordinal).ToList() : [];
        string? signed = null;
        InstallerVerificationResult? result = null;
        foreach (var candidate in candidates)
        {
            var attempt = await new AuthenticodeInstallerVerifier().VerifyAsync(candidate, TestContext.Current.CancellationToken);
            if (attempt.IsValid)
            {
                signed = candidate;
                result = attempt;
                break;
            }
        }

        Assert.SkipWhen(signed is null, "No signed .msi found on this machine.");
        Assert.False(string.IsNullOrWhiteSpace(result!.Signer));
        var pinned = await new AuthenticodeInstallerVerifier { ExpectedPublishers = [result.Signer!] }.VerifyAsync(signed!, TestContext.Current.CancellationToken);
        Assert.True(pinned.IsValid, pinned.Reason);
    }
}
