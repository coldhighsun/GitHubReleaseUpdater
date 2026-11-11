using System.Net;

namespace GitHubReleaseUpdater.Tests;

/// <summary>
/// Argument validation of the sample CLI: a bad invocation must fail with exit code 2 before any network call,
/// instead of throwing an unhandled exception or exiting the process. Every test routes the CLI through a
/// <see cref="StubHttpHandler"/> and captured writers, so a regression can never reach the real GitHub API or console.
/// </summary>
public class CliTests
{
    /// <summary>
    /// Required options shared by every test, so validation reaches the option under test.
    /// </summary>
    private static readonly string[] RequiredArgs = ["check", "--owner", "o", "--repo", "r", "--current", "1.0.0"];

    /// <summary>
    /// Stub that records every request the CLI makes; unmatched requests get a 404 instead of hitting the network.
    /// </summary>
    private readonly StubHttpHandler _handler = new();

    /// <summary>
    /// Captures what the CLI writes to standard output.
    /// </summary>
    private readonly StringWriter _stdout = new();

    /// <summary>
    /// Captures what the CLI writes to standard error.
    /// </summary>
    private readonly StringWriter _stderr = new();

    /// <summary>
    /// A malformed <c>--sha256</c> value is rejected before any request is sent.
    /// </summary>
    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("sha512:0123")]
    public async Task RunAsync_InvalidSha256_ReturnsUsageErrorCode(string sha256)
    {
        var exitCode = await RunAsync([.. RequiredArgs, "--sha256", sha256]);

        Assert.Equal(2, exitCode);
        Assert.Contains("is not a valid SHA-256 hex digest", _stderr.ToString());
        Assert.Empty(_handler.Requests);
    }

    /// <summary>
    /// <c>--sha256</c> given as a bare flag, without a value, is rejected before any request is sent.
    /// </summary>
    [Fact]
    public async Task RunAsync_Sha256WithoutValue_ReturnsUsageErrorCode()
    {
        var exitCode = await RunAsync([.. RequiredArgs, "--sha256"]);

        Assert.Equal(2, exitCode);
        Assert.Empty(_handler.Requests);
    }

    /// <summary>
    /// <c>--sha256</c> combined with <c>--no-verify</c> is contradictory and rejected, even when the digest is
    /// malformed, instead of the digest being silently ignored.
    /// </summary>
    [Theory]
    [InlineData("garbage")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task RunAsync_Sha256WithNoVerify_ReturnsUsageErrorCode(string sha256)
    {
        var exitCode = await RunAsync([.. RequiredArgs, "--no-verify", "--sha256", sha256]);

        Assert.Equal(2, exitCode);
        Assert.Contains("cannot be used together", _stderr.ToString());
        Assert.Empty(_handler.Requests);
    }

    /// <summary>
    /// An empty or blank <c>--asset</c> pattern is rejected before any request is sent.
    /// </summary>
    [Theory]
    [InlineData("--asset")]
    [InlineData("--asset= ")]
    public async Task RunAsync_EmptyAssetPattern_ReturnsUsageErrorCode(string assetArg)
    {
        var exitCode = await RunAsync([.. RequiredArgs, assetArg]);

        Assert.Equal(2, exitCode);
        Assert.Empty(_handler.Requests);
    }

    /// <summary>
    /// A missing required option returns exit code 2 instead of terminating the process.
    /// </summary>
    [Theory]
    [InlineData("check", "--repo", "r", "--current", "1.0.0")]
    [InlineData("check", "--owner", "o", "--current", "1.0.0")]
    [InlineData("check", "--owner", "o", "--repo", "r")]
    [InlineData("download", "--owner", "o", "--repo", "r", "--current", "1.0.0")]
    public async Task RunAsync_MissingRequiredOption_ReturnsUsageErrorCode(params string[] args)
    {
        var exitCode = await RunAsync(args);

        Assert.Equal(2, exitCode);
        Assert.Contains("Missing required option", _stderr.ToString());
        Assert.Empty(_handler.Requests);
    }

    /// <summary>
    /// Several missing required options are each reported, but the usage text is printed only once.
    /// </summary>
    [Fact]
    public async Task RunAsync_SeveralMissingRequiredOptions_ReportsEachAndPrintsUsageOnce()
    {
        var exitCode = await RunAsync(["download"]);

        var stderr = _stderr.ToString();
        Assert.Equal(2, exitCode);
        foreach (var key in new[] { "owner", "repo", "current", "out" })
        {
            Assert.Contains($"Missing required option --{key}.", stderr);
        }
        Assert.Equal(1, CountOccurrences(stderr, "Usage:"));
        Assert.Empty(_handler.Requests);
    }

    /// <summary>
    /// The help text documents that <c>--sha256</c> and <c>--no-verify</c> are mutually exclusive, since combining
    /// them is rejected.
    /// </summary>
    [Fact]
    public async Task RunAsync_Help_DocumentsSha256AndNoVerifyAreExclusive()
    {
        var exitCode = await RunAsync(["--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Cannot be combined with --no-verify.", _stdout.ToString());
        Assert.Contains("Cannot be combined with --sha256.", _stdout.ToString());
    }

    /// <summary>
    /// A cancelled token makes the CLI report the cancellation and exit with code 130.
    /// </summary>
    [Fact]
    public async Task RunAsync_Cancelled_ReturnsCancelledExitCode()
    {
        _handler.On("/repos/o/r/releases/latest", HttpStatusCode.OK, TestData.Read("release-latest.json"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exitCode = await RunAsync(RequiredArgs, cancellationToken: cts.Token);

        Assert.Equal(130, exitCode);
        Assert.Contains("Cancelled.", _stderr.ToString());
    }

    /// <summary>
    /// Without <c>--token</c> the default token (the <c>GITHUB_TOKEN</c> fallback) is sent; an explicit
    /// <c>--token</c> takes precedence over it.
    /// </summary>
    [Theory]
    [InlineData(null, "env-token")]
    [InlineData("cli-token", "cli-token")]
    public async Task RunAsync_Token_PrefersOptionOverDefault(string? tokenOption, string expectedToken)
    {
        _handler.On("/repos/o/r/releases/latest", HttpStatusCode.OK, TestData.Read("release-latest.json"));
        string[] args = tokenOption is null
            ? ["check", "--owner", "o", "--repo", "r", "--current", "2.1.0"]
            : ["check", "--owner", "o", "--repo", "r", "--current", "2.1.0", "--token", tokenOption];

        var exitCode = await RunAsync(args, defaultToken: "env-token");

        Assert.Equal(0, exitCode);
        Assert.Equal(expectedToken, _handler.Requests.Single().Headers.Authorization?.Parameter);
    }

    /// <summary>
    /// A valid invocation sends its requests through the injected client and writes to the injected output.
    /// </summary>
    [Fact]
    public async Task RunAsync_CheckWhenUpToDate_UsesInjectedClientAndReturnsSuccess()
    {
        _handler.On("/repos/o/r/releases/latest", HttpStatusCode.OK, TestData.Read("release-latest.json"));

        var exitCode = await RunAsync(["check", "--owner", "o", "--repo", "r", "--current", "2.1.0"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("You are up to date.", _stdout.ToString());
        Assert.Single(_handler.Requests);
    }

    /// <summary>
    /// The ETA keeps its hours instead of wrapping at 60 minutes.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, "00:00")]
    [InlineData(0, 59, 59, "59:59")]
    [InlineData(1, 5, 0, "1:05:00")]
    [InlineData(27, 3, 9, "27:03:09")]
    public void FormatEta_VariousDurations_IncludesHoursOnlyWhenNeeded(int hours, int minutes, int seconds, string expected)
    {
        var remaining = new TimeSpan(hours, minutes, seconds);

        var formatted = Cli.FormatEta(remaining);

        Assert.Equal(expected, formatted);
    }

    /// <summary>
    /// Runs the CLI against the stub handler and captured writers, with no ambient token unless
    /// <paramref name="defaultToken"/> is given.
    /// </summary>
    private Task<int> RunAsync(string[] args, string? defaultToken = null, CancellationToken cancellationToken = default)
        => Cli.RunAsync(args, _stdout, _stderr, new HttpClient(_handler), defaultToken, cancellationToken);

    /// <summary>
    /// Counts the non-overlapping occurrences of <paramref name="value"/> in <paramref name="text"/>.
    /// </summary>
    private static int CountOccurrences(string text, string value) => text.Split(value).Length - 1;
}
