using GitHubReleaseUpdater.Download;

namespace GitHubReleaseUpdater.Tests;

public class DownloadProgressTests
{
    [Fact]
    public void Percentage_is_null_when_total_is_unknown()
    {
        var p = new DownloadProgress(50, null, TimeSpan.FromSeconds(1));
        Assert.Null(p.Percentage);
    }

    [Fact]
    public void Percentage_computes_from_received_and_total()
    {
        var p = new DownloadProgress(25, 100, TimeSpan.FromSeconds(1));
        Assert.Equal(25.0, p.Percentage);
    }

    [Fact]
    public void Percentage_is_clamped_to_100_when_received_exceeds_total()
    {
        var p = new DownloadProgress(150, 100, TimeSpan.FromSeconds(1));
        Assert.Equal(100.0, p.Percentage);
    }

    [Fact]
    public void BytesPerSecond_is_zero_when_no_time_has_elapsed()
    {
        var p = new DownloadProgress(1000, 2000, TimeSpan.Zero);
        Assert.Equal(0, p.BytesPerSecond);
    }

    [Fact]
    public void BytesPerSecond_computes_average_throughput()
    {
        var p = new DownloadProgress(1000, 2000, TimeSpan.FromSeconds(2));
        Assert.Equal(500, p.BytesPerSecond);
    }

    [Fact]
    public void EstimatedRemaining_is_null_when_total_is_unknown()
    {
        var p = new DownloadProgress(1000, null, TimeSpan.FromSeconds(2));
        Assert.Null(p.EstimatedRemaining);
    }

    [Fact]
    public void EstimatedRemaining_is_null_when_no_throughput_yet()
    {
        var p = new DownloadProgress(0, 2000, TimeSpan.Zero);
        Assert.Null(p.EstimatedRemaining);
    }

    [Fact]
    public void EstimatedRemaining_is_zero_when_already_complete()
    {
        var p = new DownloadProgress(2000, 2000, TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, p.EstimatedRemaining);
    }

    [Fact]
    public void EstimatedRemaining_computes_from_current_throughput()
    {
        // 500 bytes/sec, 1000 bytes remaining -> 2 seconds.
        var p = new DownloadProgress(1000, 2000, TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), p.EstimatedRemaining);
    }

    /// <summary>
    /// Bytes carried over from an earlier attempt were not transferred during <see cref="DownloadProgress.Elapsed"/>,
    /// so they must not inflate throughput or shrink the ETA.
    /// </summary>
    [Fact]
    public void BytesPerSecond_resumed_bytes_set_excludes_them_from_throughput_and_eta()
    {
        // 1500 received, 1000 of them resumed -> 500 bytes in 1 second; 500 bytes remaining -> 1 second.
        var p = new DownloadProgress(1500, 2000, TimeSpan.FromSeconds(1)) { ResumedBytes = 1000 };

        var speed = p.BytesPerSecond;
        var eta = p.EstimatedRemaining;

        Assert.Equal(500, speed);
        Assert.Equal(TimeSpan.FromSeconds(1), eta);
        Assert.Equal(75.0, p.Percentage);
    }
}
