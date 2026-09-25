namespace GitHubReleaseUpdater;

/// <summary>
/// Validates caller-supplied timeouts up front, so an unusable value fails at construction instead of on every
/// request (or, for <see cref="TimeSpan.Zero"/>, silently timing out every request immediately).
/// </summary>
internal static class TimeoutGuard
{
    /// <summary>
    /// Longest finite timeout <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts
    /// (<see cref="uint.MaxValue"/> - 1 milliseconds, about 49.7 days).
    /// </summary>
    internal static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Shortest usable timeout: timers truncate to whole milliseconds, so anything shorter would fire immediately.
    /// </summary>
    internal static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> unless <paramref name="timeout"/> is null,
    /// <see cref="Timeout.InfiniteTimeSpan"/>, or between <see cref="MinTimeout"/> and <see cref="MaxTimeout"/>.
    /// </summary>
    internal static void ThrowIfInvalid(TimeSpan? timeout, string paramName)
    {
        if (timeout is { } value && value != Timeout.InfiniteTimeSpan && (value < MinTimeout || value > MaxTimeout))
        {
            throw new ArgumentOutOfRangeException(paramName, value,
                $"Timeout must be null, Timeout.InfiniteTimeSpan, or between {MinTimeout} and {MaxTimeout}.");
        }
    }
}
