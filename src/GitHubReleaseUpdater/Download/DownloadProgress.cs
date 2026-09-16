namespace GitHubReleaseUpdater.Download;

/// <summary>
/// Snapshot of a download in progress.
/// </summary>
/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="TotalBytes">Expected total, or null when the server did not report a length.</param>
/// <param name="Elapsed">Time since the download started.</param>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes, TimeSpan Elapsed)
{
    /// <summary>
    /// Completion percentage (0–100), or null when the total is unknown.
    /// </summary>
    public double? Percentage => TotalBytes is > 0 ? Math.Min(100.0, BytesReceived * 100.0 / TotalBytes.Value) : null;

    /// <summary>
    /// Average throughput in bytes per second.
    /// </summary>
    public double BytesPerSecond => Elapsed.TotalSeconds > 0 ? BytesReceived / Elapsed.TotalSeconds : 0;

    /// <summary>
    /// Estimated remaining time, or null when unknown.
    /// </summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (TotalBytes is not > 0 || BytesPerSecond <= 0) return null;
            var remaining = TotalBytes.Value - BytesReceived;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }
}
