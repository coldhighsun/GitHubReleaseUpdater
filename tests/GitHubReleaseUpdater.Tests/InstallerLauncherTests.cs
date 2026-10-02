using System.ComponentModel;
using System.Diagnostics;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Installation;
using GitHubReleaseUpdater.Verification;

namespace GitHubReleaseUpdater.Tests;

public class InstallerLauncherTests : IDisposable
{
    /// <summary>
    /// Temporary directory holding fake installer files, removed after each test.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gru-tests-" + Guid.NewGuid().ToString("N"));

    public InstallerLauncherTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Creates a small file named <paramref name="name"/> in the temp directory and returns its path.
    /// </summary>
    private string CreateInstaller(string name = "setup.exe")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    /// <summary>
    /// Builds a launcher whose process starter records the start info instead of running anything.
    /// </summary>
    private static ProcessInstallerLauncher RecordingLauncher(List<ProcessStartInfo> started, int? pid = 4242) => new(psi =>
    {
        started.Add(psi);
        return pid;
    });

    [Fact]
    public void Launch_ValidInstaller_StartsViaShellWithArgumentsAndReturnsPid()
    {
        var path = CreateInstaller();
        var started = new List<ProcessStartInfo>();
        var launcher = RecordingLauncher(started);

        var result = launcher.Launch(path, InstallerLaunchOptions.InnoSetupSilent);

        Assert.Equal(Path.GetFullPath(path), result.InstallerPath);
        Assert.Equal(4242, result.ProcessId);
        var psi = Assert.Single(started);
        Assert.True(psi.UseShellExecute);
        Assert.Equal(Path.GetFullPath(path), psi.FileName);
        Assert.Equal("/SILENT /SP- /NORESTART", psi.Arguments);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(path)), psi.WorkingDirectory);
    }

    [Fact]
    public void Launch_NoArguments_LeavesArgumentsEmpty()
    {
        var path = CreateInstaller();
        var started = new List<ProcessStartInfo>();

        RecordingLauncher(started).Launch(path, new InstallerLaunchOptions());

        Assert.Equal(string.Empty, Assert.Single(started).Arguments);
    }

    [Fact]
    public void Launch_ExplicitWorkingDirectory_UsesIt()
    {
        var path = CreateInstaller();
        var started = new List<ProcessStartInfo>();

        RecordingLauncher(started).Launch(path, new InstallerLaunchOptions { WorkingDirectory = _dir + "-other" });

        Assert.Equal(_dir + "-other", Assert.Single(started).WorkingDirectory);
    }

    [Fact]
    public void Launch_MissingFile_ThrowsInstallerLaunchExceptionWithoutStarting()
    {
        var started = new List<ProcessStartInfo>();
        var missing = Path.Combine(_dir, "missing.exe");

        var ex = Assert.Throws<InstallerLaunchException>(() => RecordingLauncher(started).Launch(missing, new InstallerLaunchOptions()));

        Assert.Equal(Path.GetFullPath(missing), ex.FilePath);
        Assert.False(ex.IsUserCancelled);
        Assert.Empty(started);
    }

    [Fact]
    public void Launch_UserDeclinesElevation_MapsToCancelledException()
    {
        var path = CreateInstaller();
        var launcher = new ProcessInstallerLauncher(_ => throw new Win32Exception(1223, "The operation was canceled by the user."));

        var ex = Assert.Throws<InstallerLaunchException>(() => launcher.Launch(path, new InstallerLaunchOptions()));

        Assert.True(ex.IsUserCancelled);
        Assert.IsType<Win32Exception>(ex.InnerException);
    }

    [Fact]
    public void Launch_OtherStartFailure_MapsToNonCancelledException()
    {
        var path = CreateInstaller();
        var launcher = new ProcessInstallerLauncher(_ => throw new Win32Exception(5, "Access is denied."));

        var ex = Assert.Throws<InstallerLaunchException>(() => launcher.Launch(path, new InstallerLaunchOptions()));

        Assert.False(ex.IsUserCancelled);
        Assert.Contains("Access is denied", ex.Message);
    }

    [Fact]
    public void Launch_NoProcessStarted_ReportsNullProcessId()
    {
        var path = CreateInstaller();

        var result = RecordingLauncher([], pid: null).Launch(path, new InstallerLaunchOptions());

        Assert.Null(result.ProcessId);
    }

    [Fact]
    public async Task LaunchInstallerAsync_RequireVerifiedWithUnverifiedDownload_ThrowsWithoutLaunching()
    {
        var launcher = new RecordingInstallerLauncher();
        using var updater = new ReleaseUpdater(Options(launcher), new FakeReleaseClient());
        var path = CreateInstaller();
        var download = new DownloadResult(path, TestData.Release("v2.0.0", "setup.exe").Assets[0], Hash(path), verified: false);

        await Assert.ThrowsAsync<UpdaterException>(() => updater.LaunchInstallerAsync(download, new InstallerLaunchOptions { RequireVerified = true }));

        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task LaunchInstallerAsync_RequireVerifiedWithVerifiedDownload_Launches()
    {
        var launcher = new RecordingInstallerLauncher();
        using var updater = new ReleaseUpdater(Options(launcher), new FakeReleaseClient());
        var path = CreateInstaller();
        var download = new DownloadResult(path, TestData.Release("v2.0.0", "setup.exe").Assets[0], Hash(path), verified: true);

        await updater.LaunchInstallerAsync(download, new InstallerLaunchOptions { RequireVerified = true, Arguments = "/S" });

        var (launchedPath, launchedOptions) = Assert.Single(launcher.Launched);
        Assert.Equal(path, launchedPath);
        Assert.Equal("/S", launchedOptions.Arguments);
    }

    [Fact]
    public async Task LaunchInstallerAsync_FileModifiedAfterDownload_ThrowsChecksumMismatchWithoutLaunching()
    {
        var launcher = new RecordingInstallerLauncher();
        using var updater = new ReleaseUpdater(Options(launcher), new FakeReleaseClient());
        var path = CreateInstaller();
        var download = new DownloadResult(path, TestData.Release("v2.0.0", "setup.exe").Assets[0], Hash(path), verified: true);
        await File.WriteAllBytesAsync(path, [6, 6, 6], TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ChecksumMismatchException>(() => updater.LaunchInstallerAsync(download));

        Assert.Equal(path, ex.FilePath);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task LaunchInstallerAsync_FileDeletedAfterDownload_ThrowsInstallerLaunchException()
    {
        var launcher = new RecordingInstallerLauncher();
        using var updater = new ReleaseUpdater(Options(launcher), new FakeReleaseClient());
        var path = CreateInstaller();
        var download = new DownloadResult(path, TestData.Release("v2.0.0", "setup.exe").Assets[0], Hash(path), verified: true);
        File.Delete(path);

        await Assert.ThrowsAsync<InstallerLaunchException>(() => updater.LaunchInstallerAsync(download));

        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task DownloadAndLaunchInstallerAsync_UpdateAvailable_DownloadsThenLaunchesDownloadedFile()
    {
        var launcher = new RecordingInstallerLauncher();
        var bytes = new byte[] { 9, 8, 7, 6 };
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", bytes.Length, "setup.exe") };
        client.AssetBytes["setup.exe"] = bytes;
        using var updater = new ReleaseUpdater(Options(launcher), client);
        var check = await updater.CheckForUpdateAsync();

        var result = await updater.DownloadAndLaunchInstallerAsync(check, _dir, InstallerLaunchOptions.InnoSetupSilent);

        var expectedPath = Path.Combine(_dir, "setup.exe");
        Assert.Equal(expectedPath, result.InstallerPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expectedPath));
        var (launchedPath, launchedOptions) = Assert.Single(launcher.Launched);
        Assert.Equal(expectedPath, launchedPath);
        Assert.Same(InstallerLaunchOptions.InnoSetupSilent, launchedOptions);
    }

    [Fact]
    public async Task DownloadAndLaunchInstallerAsync_ChecksumMismatch_DoesNotLaunch()
    {
        var launcher = new RecordingInstallerLauncher();
        var bytes = new byte[] { 9, 8, 7, 6 };
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", bytes.Length, "setup.exe") };
        client.AssetBytes["setup.exe"] = bytes;
        using var updater = new ReleaseUpdater(Options(launcher, new StaticChecksumProvider(new string('0', 64))), client);
        var check = await updater.CheckForUpdateAsync();

        await Assert.ThrowsAsync<ChecksumMismatchException>(() => updater.DownloadAndLaunchInstallerAsync(check, _dir));

        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task DownloadAndLaunchInstallerAsync_NoUpdate_ThrowsWithoutLaunching()
    {
        var launcher = new RecordingInstallerLauncher();
        var client = new FakeReleaseClient { Latest = TestData.Release("v1.0.0", "setup.exe") };
        using var updater = new ReleaseUpdater(Options(launcher), client);
        var check = await updater.CheckForUpdateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAndLaunchInstallerAsync(check, _dir));

        Assert.Empty(launcher.Launched);
    }

    /// <summary>
    /// SHA-256 of the file at <paramref name="path"/> as lowercase hex.
    /// </summary>
    private static string Hash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Builds options at version 1.0.0 selecting the single <c>setup.exe</c> asset and using <paramref name="launcher"/>.
    /// </summary>
    private static UpdaterOptions Options(IInstallerLauncher launcher, IChecksumProvider? checksums = null) => new()
    {
        Owner = "o",
        Repo = "r",
        CurrentVersion = "1.0.0",
        AssetSelector = new DelegateAssetSelector(a => a.Name == "setup.exe"),
        ChecksumProvider = checksums ?? NoChecksumProvider.Instance,
        InstallerLauncher = launcher,
    };

    /// <summary>
    /// <see cref="IInstallerLauncher"/> that records its calls instead of starting a process.
    /// </summary>
    private sealed class RecordingInstallerLauncher : IInstallerLauncher
    {
        /// <summary>
        /// Every (path, options) pair passed to <see cref="Launch"/>, in order.
        /// </summary>
        public List<(string Path, InstallerLaunchOptions Options)> Launched { get; } = [];

        public InstallerLaunchResult Launch(string installerPath, InstallerLaunchOptions options)
        {
            Launched.Add((installerPath, options));
            return new InstallerLaunchResult(installerPath, null);
        }
    }
}
