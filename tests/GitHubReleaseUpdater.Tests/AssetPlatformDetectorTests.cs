using GitHubReleaseUpdater.Assets;

namespace GitHubReleaseUpdater.Tests;

public class AssetPlatformDetectorTests
{
    /// <summary>
    /// Fixed Windows x64 runtime used so results are deterministic regardless of the host OS.
    /// </summary>
    private static readonly RuntimeInfo Win64 = new("win", "x64");

    [Theory]
    [InlineData("app-win-x64.zip", "win", "x64")]
    [InlineData("app_windows_amd64.zip", "win", "x64")]
    [InlineData("app-linux-x86_64.tar.gz", "linux", "x64")]
    [InlineData("app-x86-64-linux.tar.gz", "linux", "x64")]
    [InlineData("app_linux_aarch64.tar.gz", "linux", "arm64")]
    [InlineData("app-darwin-arm64.dmg", "osx", "arm64")]
    [InlineData("App-macOS-x64.pkg", "osx", "x64")]
    [InlineData("app-windows-i386.msi", "win", "x86")]
    [InlineData("app-linux-armv7.tar.gz", "linux", "arm")]
    [InlineData("app-freebsd-amd64.txz", "freebsd", "x64")]
    [InlineData("app-win64.zip", "win", null)]
    [InlineData("app-linux.tar.gz", "linux", null)]
    [InlineData("app-arm64.zip", null, "arm64")]
    public void Detect_KnownNames_ReturnsCanonicalOsAndArch(string name, string? os, string? arch)
    {
        var platform = AssetPlatformDetector.Detect(name);

        Assert.Equal(os, platform.Os);
        Assert.Equal(arch, platform.Arch);
    }

    [Theory]
    [InlineData("app-setup.exe")]
    [InlineData("source.zip")]
    public void Detect_NameWithoutPlatform_IsUnknown(string name)
    {
        var platform = AssetPlatformDetector.Detect(name);

        Assert.True(platform.IsUnknown);
        Assert.Equal(string.Empty, platform.Rid);
    }

    [Fact]
    public void Rid_OsAndArch_JoinsThem()
    {
        Assert.Equal("win-x64", new AssetPlatform("win", "x64").Rid);
        Assert.Equal("linux", new AssetPlatform("linux", null).Rid);
        Assert.Equal("arm64", new AssetPlatform(null, "arm64").Rid);
    }

    [Fact]
    public void Describe_MixedRelease_ReportsPlatformMetadataAndCurrentMatch()
    {
        var release = TestData.Release("v1.0.0",
            "app-win-x64.zip", "app-linux-amd64.tar.gz", "app-win-arm64.zip", "app-setup.exe", "app-win-x64.zip.sha256", "SHA256SUMS");

        var described = AssetPlatformDetector.Describe(release, Win64);

        Assert.Equal(6, described.Count);
        Assert.Equal(["app-win-x64.zip"], described.Where(d => d.MatchesCurrent).Select(d => d.Asset.Name));
        Assert.Equal(["app-win-x64.zip.sha256", "SHA256SUMS"], described.Where(d => d.IsMetadata).Select(d => d.Asset.Name));
        Assert.True(described.Single(d => d.Asset.Name == "app-setup.exe").Platform.IsUnknown);
        Assert.Equal("linux", described.Single(d => d.Asset.Name == "app-linux-amd64.tar.gz").Platform.Os);
    }

    [Fact]
    public void Describe_OsOnlyName_MatchesWhenArchIsUnspecified()
    {
        var release = TestData.Release("v1.0.0", "app-windows.zip", "app-linux.tar.gz");

        var described = AssetPlatformDetector.Describe(release, Win64);

        Assert.Equal(["app-windows.zip"], described.Where(d => d.MatchesCurrent).Select(d => d.Asset.Name));
    }

    [Fact]
    public async Task ListAssets_Check_ExcludesMetadataUnlessRequested()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip", "app-linux-x64.tar.gz", "app-win-x64.zip.sha256") };
        using var updater = new ReleaseUpdater(new UpdaterOptions { Owner = "o", Repo = "r", CurrentVersion = "1.0.0" }, client);
        var check = await updater.CheckForUpdateAsync();

        var assets = updater.ListAssets(check);
        var withMetadata = updater.ListAssets(check, includeMetadata: true);

        Assert.Equal(["app-win-x64.zip", "app-linux-x64.tar.gz"], assets.Select(a => a.Asset.Name));
        Assert.Equal(3, withMetadata.Count);
    }

    [Fact]
    public async Task ListAssets_NoReleaseFound_ReturnsEmpty()
    {
        var client = new FakeReleaseClient { Latest = null };
        using var updater = new ReleaseUpdater(new UpdaterOptions { Owner = "o", Repo = "r", CurrentVersion = "1.0.0" }, client);
        var check = await updater.CheckForUpdateAsync();

        Assert.Empty(updater.ListAssets(check));
    }
}
