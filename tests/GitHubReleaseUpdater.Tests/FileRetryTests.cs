using GitHubReleaseUpdater.LastCheck;

namespace GitHubReleaseUpdater.Tests;

/// <summary>
/// Tests for <see cref="FileRetry"/>.
/// </summary>
public sealed class FileRetryTests
{
    /// <summary>
    /// The HRESULT Windows gives an IOException for a sharing violation.
    /// </summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>
    /// A sharing violation is tried five times in all.
    /// </summary>
    [Fact]
    public async Task Sharing_violation_is_tried_five_times()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<IOException>(() => FileRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw new IOException("busy", SharingViolation);
        }, TestContext.Current.CancellationToken));

        Assert.Equal(5, attempts);
    }

    /// <summary>
    /// Access denied is retried once only, since it may be a real permission problem.
    /// </summary>
    [Fact]
    public async Task Access_denied_is_tried_twice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Access denied is only taken for a held file on Windows.");
        var attempts = 0;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => FileRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw new UnauthorizedAccessException();
        }, TestContext.Current.CancellationToken));

        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// Any other failure is thrown at once.
    /// </summary>
    [Fact]
    public async Task Other_failures_are_not_retried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => FileRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw new InvalidOperationException();
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
    }
}
