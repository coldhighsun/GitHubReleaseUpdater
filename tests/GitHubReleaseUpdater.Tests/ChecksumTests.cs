using System.Text;
using GitHubReleaseUpdater.Verification;

namespace GitHubReleaseUpdater.Tests;

public class ChecksumTests
{
    /// <summary>
    /// A sample SHA-256 hex digest used across test cases.
    /// </summary>
    private const string HashA = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void ParseSums_handles_gnu_binary_bsd_and_comments()
    {
        var sums = ChecksumParser.ParseSums(TestData.Read("SHA256SUMS"));

        Assert.Equal(3, sums.Count);
        Assert.Equal(HashA, sums["myapp-2.1.0-win-x64.zip"]);
        Assert.Equal(new string('1', 64), sums["myapp-2.1.0-linux-amd64.tar.gz"]);
        Assert.Equal(new string('2', 64), sums["other.bin"]);
    }

    [Fact]
    public void ParseSums_bare_hash_is_returned_via_FindFor()
    {
        var sums = ChecksumParser.ParseSums(HashA.ToUpperInvariant() + "\r\n");
        Assert.Equal(HashA, ChecksumParser.FindFor(sums, "anything.zip"));
    }

    [Fact]
    public void ParseSums_bsd_format_is_parsed_directly()
    {
        var sums = ChecksumParser.ParseSums($"SHA256 (dist/app.zip) = {HashA}\n");
        Assert.Equal(HashA, sums["app.zip"]);
    }

    [Fact]
    public void FindFor_does_not_fall_back_to_bare_hash_when_multiple_entries_exist()
    {
        var sums = ChecksumParser.ParseSums($"{HashA}  a.zip\n{new string('1', 64)}  b.zip\n");
        Assert.Null(ChecksumParser.FindFor(sums, "missing.zip"));
    }

    [Fact]
    public void ParseSums_ignores_non_sha256_lines()
    {
        var sums = ChecksumParser.ParseSums("d41d8cd98f00b204e9800998ecf8427e  md5file\nzz\n");
        Assert.Empty(sums);
    }

    [Theory]
    [InlineData("sha256:" + HashA, HashA)]
    [InlineData("SHA256:" + HashA, HashA)]
    [InlineData(HashA, HashA)]
    [InlineData("sha512:abcd", null)]
    [InlineData("", null)]
    [InlineData("xyz", null)]
    public void NormalizeDigest(string input, string? expected)
        => Assert.Equal(expected, ChecksumParser.NormalizeDigest(input));

    [Fact]
    public async Task FileHasher_computes_sha256()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("test"));
        try
        {
            Assert.Equal(HashA, await FileHasher.Sha256Async(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReleaseChecksumProvider_prefers_sidecar_then_sums_then_digest()
    {
        var release = TestData.Release("v1", "app.zip", "app.zip.sha256", "SHA256SUMS", "other.zip", "digest-only.zip");
        var client = new FakeReleaseClient();
        client.AssetBytes["app.zip.sha256"] = Encoding.ASCII.GetBytes(new string('a', 64) + "  app.zip\n");
        client.AssetBytes["SHA256SUMS"] = Encoding.ASCII.GetBytes(new string('b', 64) + "  app.zip\n" + new string('c', 64) + "  other.zip\n");
        var provider = new ReleaseChecksumProvider(client);

        Assert.Equal(new string('a', 64), await provider.GetExpectedSha256Async(release, release.Assets[0]));
        Assert.Equal(new string('c', 64), await provider.GetExpectedSha256Async(release, release.Assets[3]));

        var withDigest = new GitHub.Models.GitHubAsset { Name = "digest-only.zip", Digest = "sha256:" + new string('d', 64) };
        Assert.Equal(new string('d', 64), await provider.GetExpectedSha256Async(release, withDigest));

        var none = new GitHub.Models.GitHubAsset { Name = "none.zip" };
        Assert.Null(await provider.GetExpectedSha256Async(release, none));
    }

    [Fact]
    public async Task Composite_returns_first_non_null()
    {
        var release = TestData.Release("v1", "a");
        var composite = new CompositeChecksumProvider(NoChecksumProvider.Instance, new StaticChecksumProvider(HashA));
        Assert.Equal(HashA, await composite.GetExpectedSha256Async(release, release.Assets[0]));
    }

    [Fact]
    public void StaticChecksumProvider_rejects_invalid()
        => Assert.Throws<ArgumentException>(() => new StaticChecksumProvider("nope"));

    [Fact]
    public void FindFor_returns_null_when_multiple_entries_and_file_not_present()
    {
        var sums = ChecksumParser.ParseSums(HashA + "  a.zip\n" + new string('1', 64) + "  b.zip\n");
        Assert.Null(ChecksumParser.FindFor(sums, "missing.zip"));
    }
}
