using GitHubReleaseUpdater.Assets;

namespace GitHubReleaseUpdater.Tests;

public class AssetSelectorTests
{
    /// <summary>
    /// Representative asset file names covering common release naming conventions across platforms.
    /// </summary>
    private static readonly string[] TypicalAssets =
    [
        "myapp-1.2.3-win-x64.zip",
        "myapp-1.2.3-win-x64.zip.sha256",
        "myapp-1.2.3-win-arm64.zip",
        "myapp-1.2.3-win-x86.msi",
        "myapp_1.2.3_linux_amd64.tar.gz",
        "myapp_1.2.3_linux_arm64.tar.gz",
        "myapp-1.2.3-darwin-arm64.dmg",
        "myapp-1.2.3-macos-x86_64.dmg",
        "SHA256SUMS",
        "checksums.txt",
    ];

    [Theory]
    [InlineData("win", "x64", "myapp-1.2.3-win-x64.zip")]
    [InlineData("win", "arm64", "myapp-1.2.3-win-arm64.zip")]
    [InlineData("win", "x86", "myapp-1.2.3-win-x86.msi")]
    [InlineData("linux", "x64", "myapp_1.2.3_linux_amd64.tar.gz")]
    [InlineData("linux", "arm64", "myapp_1.2.3_linux_arm64.tar.gz")]
    [InlineData("osx", "arm64", "myapp-1.2.3-darwin-arm64.dmg")]
    [InlineData("osx", "x64", "myapp-1.2.3-macos-x86_64.dmg")]
    public void Runtime_selector_matches_platform_aliases(string os, string arch, string expected)
    {
        var release = TestData.Release("v1.2.3", TypicalAssets);
        var selected = new RuntimeAssetSelector(new RuntimeInfo(os, arch)).Select(release);
        Assert.Equal(expected, selected?.Name);
    }

    [Fact]
    public void Runtime_selector_never_picks_checksum_files()
    {
        var release = TestData.Release("v1", "SHA256SUMS", "win-x64.sha256", "checksums.txt");
        Assert.Null(new RuntimeAssetSelector(new RuntimeInfo("win", "x64")).Select(release));
    }

    [Fact]
    public void Runtime_selector_still_picks_plain_txt_release_asset()
    {
        // A .txt file is only metadata when its name looks like a checksum/sums file (see IsMetadataFile tests below);
        // a plain text release artifact must remain selectable.
        var release = TestData.Release("v1", "app-win-x64.txt", "readme.txt");
        Assert.Equal("app-win-x64.txt", new RuntimeAssetSelector(new RuntimeInfo("win", "x64")).Select(release)?.Name);
    }

    [Theory]
    [InlineData("app.sha256", true)]
    [InlineData("app.sha512", true)]
    [InlineData("app.sha1", true)]
    [InlineData("app.md5", true)]
    [InlineData("app.sig", true)]
    [InlineData("app.asc", true)]
    [InlineData("app.pem", true)]
    [InlineData("app.sbom", true)]
    [InlineData("SHA256SUMS", true)]
    [InlineData("sha256sums.txt", true)]
    [InlineData("checksums.txt", true)]
    [InlineData("myapp_1.2.3_checksums.txt", true)]
    [InlineData("myapp-SHA256SUMS.txt", true)]
    [InlineData("sha256sum.txt", true)]
    [InlineData("md5sums.txt", false)]
    [InlineData("app-win-x64.zip", false)]
    [InlineData("readme.txt", false)]
    [InlineData("app-notes.txt", false)]
    [InlineData("install-instructions.TXT", false)]
    public void IsMetadataFile_classifies_by_name(string name, bool expected)
        => Assert.Equal(expected, RuntimeAssetSelector.IsMetadataFile(name));

    [Fact]
    public void Runtime_selector_rejects_other_platform_even_when_arch_matches()
    {
        var release = TestData.Release("v1", "app-linux-x64.tar.gz");
        Assert.Null(new RuntimeAssetSelector(new RuntimeInfo("win", "x64")).Select(release));
    }

    [Fact]
    public void Runtime_selector_accepts_os_only_asset()
    {
        var release = TestData.Release("v1", "app-windows.zip", "app-linux.tar.gz");
        Assert.Equal("app-windows.zip", new RuntimeAssetSelector(new RuntimeInfo("win", "x64")).Select(release)?.Name);
    }

    [Fact]
    public void Runtime_selector_honours_extension_preference()
    {
        var release = TestData.Release("v1", "app-win-x64.zip", "app-win-x64.msi", "app-win-x64.exe");
        Assert.Equal("app-win-x64.msi", new RuntimeAssetSelector(new RuntimeInfo("win", "x64"), ".msi", ".zip").Select(release)?.Name);
        Assert.Equal("app-win-x64.exe", new RuntimeAssetSelector(new RuntimeInfo("win", "x64"), "exe").Select(release)?.Name);
    }

    [Fact]
    public void Pattern_selector_expands_placeholders()
    {
        var release = TestData.Release("v1.2.3", TypicalAssets);
        var rt = new RuntimeInfo("win", "x64");
        Assert.Equal("myapp-1.2.3-win-x64.zip", new PatternAssetSelector("myapp-{version}-{rid}.zip", rt).Select(release)?.Name);
        Assert.Equal("myapp-1.2.3-win-x64.zip", new PatternAssetSelector("myapp-*-{os}-{arch}.zip", rt).Select(release)?.Name);
        Assert.Equal("myapp_1.2.3_linux_amd64.tar.gz", new PatternAssetSelector("*linux_amd64*", rt).Select(release)?.Name);
        Assert.Equal("SHA256SUMS", new PatternAssetSelector("sha256sums", rt).Select(release)?.Name);
        Assert.Null(new PatternAssetSelector("nothing-{tag}.zip", rt).Select(release));
    }

    [Fact]
    public void Pattern_selector_escapes_regex_metacharacters()
    {
        var release = TestData.Release("v1", "a+b(1).zip", "axb1.zip");
        Assert.Equal("a+b(1).zip", new PatternAssetSelector("a+b(1).zip").Select(release)?.Name);
    }

    [Fact]
    public void Delegate_selector_uses_predicate()
    {
        var release = TestData.Release("v1", "a.zip", "b.zip");
        Assert.Equal("b.zip", new DelegateAssetSelector(a => a.Name.StartsWith('b')).Select(release)?.Name);
        Assert.Equal("a.zip", new DelegateAssetSelector(r => r.Assets[0]).Select(release)?.Name);
    }

    [Fact]
    public void RuntimeInfo_rid_combines_os_and_arch()
        => Assert.Equal("win-x64", new RuntimeInfo("win", "x64").Rid);

    [Fact]
    public void RuntimeInfo_current_detects_a_known_os_and_arch()
    {
        Assert.Contains(RuntimeInfo.Current.Os, new[] { "win", "osx", "linux", "freebsd", "unknown" });
        Assert.False(string.IsNullOrEmpty(RuntimeInfo.Current.Arch));
    }
}
