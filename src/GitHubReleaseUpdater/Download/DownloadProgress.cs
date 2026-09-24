namespace GitHubReleaseUpdater.Download;

/// <summary>
/// Snapshot of a download in progress.
/// </summary>
/// <param name="BytesReceived">Bytes written so far, including any <see cref="ResumedBytes"/> carried over from an earlier attempt.</param>
/// <param name="TotalBytes">Expected total, or null when the server did not report a length.</param>
/// <param name="Elapsed">Time since the current transfer started (a resumed transfer starts its own clock).</param>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes, TimeSpan Elapsed)
{
    /// <summary>
    /// Bytes already on disk from an earlier attempt when the current transfer resumed; 0 for a fresh transfer.
    /// They count towards <see cref="BytesReceived"/> and <see cref="Percentage"/> but not towards
    /// <see cref="BytesPerSecond"/>, since they were not transferred during <see cref="Elapsed"/>.
    /// </summary>
    public long ResumedBytes { get; init; }

    /// <summary>
    /// Completion percentage (0–100), or null when the total is unknown.
    /// </summary>
    public double? Percentage => TotalBytes is > 0 ? Math.Min(100.0, BytesReceived * 100.0 / TotalBytes.Value) : null;

    /// <summary>
    /// Average throughput of the current transfer in bytes per second (excluding <see cref="ResumedBytes"/>).
    /// </summary>
    public double BytesPerSecond => Elapsed.TotalSeconds > 0 ? Math.Max(0, BytesReceived - ResumedBytes) / Elapsed.TotalSeconds : 0;

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
