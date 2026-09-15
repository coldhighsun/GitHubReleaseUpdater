using System.Security.Cryptography;
using System.Text;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Verification;

namespace GitHubReleaseUpdater.Tests;

public class ReleaseUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gru-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly RuntimeInfo Win64 = new("win", "x64");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static UpdaterOptions Options(string current, bool prerelease = false, IChecksumProvider? checksums = null, bool require = false) => new()
    {
        Owner = "o",
        Repo = "r",
        CurrentVersion = current,
        IncludePrerelease = prerelease,
        AssetSelector = new RuntimeAssetSelector(Win64),
        ChecksumProvider = checksums,
        RequireChecksum = require,
    };

    [Fact]
    public async Task Reports_update_when_latest_is_newer()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip", "app-linux-x64.tar.gz") };
        using var updater = new ReleaseUpdater(Options("1.5.0"), client);

        var result = await updater.CheckForUpdateAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("2.0.0", result.LatestVersion!.ToString());
        Assert.Equal("app-win-x64.zip", result.SelectedAsset?.Name);
        Assert.Empty(result.SkippedTags);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("2.0.1")]
    [InlineData("3.0.0-beta")]
    public async Task No_update_when_current_is_same_or_newer(string current)
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip") };
        using var updater = new ReleaseUpdater(Options(current), client);

        var result = await updater.CheckForUpdateAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.SelectedAsset);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(result, _dir));
    }

    [Fact]
    public async Task No_release_yields_null_latest()
    {
        using var updater = new ReleaseUpdater(Options("1.0.0"), new FakeReleaseClient());
        var result = await updater.CheckForUpdateAsync();
        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task Prerelease_mode_picks_highest_semver_and_skips_drafts_and_unparseable()
    {
        var client = new FakeReleaseClient();
        client.All.AddRange(
        [
            TestData.Release("v3.0.0-beta.2"),
            TestData.Release("v3.0.0-beta.10"),
            new() { TagName = "v9.9.9", Draft = true },
            TestData.Release("v2.1.0"),
            TestData.Release("nightly-2026-09-01"),
        ]);
        using var updater = new ReleaseUpdater(Options("2.1.0", prerelease: true), client);

        var result = await updater.CheckForUpdateAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("3.0.0-beta.10", result.LatestVersion!.ToString());
        Assert.Equal(["nightly-2026-09-01"], result.SkippedTags);
    }

    [Fact]
    public async Task Stable_mode_ignores_prereleases_via_latest_endpoint()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.1.0", "app-win-x64.zip") };
        client.All.Add(TestData.Release("v3.0.0-beta.1"));
        using var updater = new ReleaseUpdater(Options("2.1.0"), client);
        Assert.False((await updater.CheckForUpdateAsync()).IsUpdateAvailable);
    }

    [Fact]
    public async Task Download_verifies_against_release_sums_file()
    {
        var payload = Encoding.ASCII.GetBytes("hello world");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip", "SHA256SUMS") };
        client.AssetBytes["app-win-x64.zip"] = payload;
        client.AssetBytes["SHA256SUMS"] = Encoding.ASCII.GetBytes($"{hash}  app-win-x64.zip\n");
        using var updater = new ReleaseUpdater(Options("1.0.0"), client);

        var check = await updater.CheckForUpdateAsync();
        var download = await updater.DownloadAsync(check, _dir);

        Assert.True(download.Verified);
        Assert.Equal(hash, download.Sha256);
        Assert.True(File.Exists(download.FilePath));
    }

    [Fact]
    public async Task Download_mismatch_deletes_file_and_throws()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip") };
        client.AssetBytes["app-win-x64.zip"] = [1, 2, 3];
        using var updater = new ReleaseUpdater(Options("1.0.0", checksums: new StaticChecksumProvider(new string('f', 64))), client);

        var check = await updater.CheckForUpdateAsync();
        var ex = await Assert.ThrowsAsync<ChecksumMismatchException>(() => updater.DownloadAsync(check, _dir));

        Assert.Equal(new string('f', 64), ex.Expected);
        Assert.False(File.Exists(ex.FilePath));
    }

    [Fact]
    public async Task Download_without_checksum_is_unverified_unless_required()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip") };
        client.AssetBytes["app-win-x64.zip"] = [1, 2, 3];

        using (var updater = new ReleaseUpdater(Options("1.0.0"), client))
        {
            var result = await updater.DownloadAsync(await updater.CheckForUpdateAsync(), _dir);
            Assert.False(result.Verified);
            File.Delete(result.FilePath);
        }

        using (var strict = new ReleaseUpdater(Options("1.0.0", require: true), client))
        {
            await Assert.ThrowsAsync<UpdaterException>(async () => await strict.DownloadAsync(await strict.CheckForUpdateAsync(), _dir));
            Assert.Empty(Directory.GetFiles(_dir, "*.zip"));
        }
    }

    [Fact]
    public async Task Download_without_matching_asset_throws_with_available_names()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-linux-x64.tar.gz") };
        using var updater = new ReleaseUpdater(Options("1.0.0"), client);

        var check = await updater.CheckForUpdateAsync();
        Assert.True(check.IsUpdateAvailable);
        Assert.Null(check.SelectedAsset);

        var ex = await Assert.ThrowsAsync<AssetNotFoundException>(() => updater.DownloadAsync(check, _dir));
        Assert.Equal(["app-linux-x64.tar.gz"], ex.AvailableAssets);
    }

    [Fact]
    public async Task Facade_end_to_end_over_http_stub()
    {
        var payload = Encoding.ASCII.GetBytes("test");
        var handler = new StubHttpHandler()
            .On("/repos/o/r/releases/latest", System.Net.HttpStatusCode.OK, TestData.Read("release-latest.json"))
            .OnBytes("/releases/assets/1", payload)
            .On("/releases/assets/3", System.Net.HttpStatusCode.OK, TestData.Read("SHA256SUMS"), "text/plain");
        using var updater = new ReleaseUpdater(new UpdaterOptions
        {
            Owner = "o",
            Repo = "r",
            CurrentVersion = "2.0.0",
            AssetSelector = new PatternAssetSelector("myapp-{version}-{rid}.zip", Win64),
            HttpClient = new HttpClient(handler),
        });

        var check = await updater.CheckForUpdateAsync();
        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("myapp-2.1.0-win-x64.zip", check.SelectedAsset!.Name);

        var download = await updater.DownloadAsync(check, _dir);
        Assert.True(download.Verified);
        Assert.Equal("test", await File.ReadAllTextAsync(download.FilePath));
    }
}
