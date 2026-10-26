using System.Runtime.InteropServices;

namespace GitHubReleaseUpdater.LastCheck;

/// <summary>
/// Runs a file operation again a few times when another process briefly has the file open, as antivirus and indexing
/// tools do right after a file was written.
/// </summary>
internal static class FileRetry
{
    /// <summary>
    /// Attempts made in total.
    /// </summary>
    private const int MaxAttempts = 5;

    /// <summary>
    /// Attempts made in total when the failure is "access denied", which may also be a real permission problem and then
    /// only costs one extra pause.
    /// </summary>
    private const int MaxAttemptsWhenDenied = 2;

    /// <summary>
    /// Win32 error code for a sharing violation (the file is open in another process).
    /// </summary>
    private const int ErrorSharingViolation = 32;

    /// <summary>
    /// Win32 error code for a lock violation (part of the file is locked by another process).
    /// </summary>
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Pause between attempts.
    /// </summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Runs <paramref name="operation"/>, trying again when the file is in use. Any other failure, and a lock that
    /// outlasts the attempts, is thrown to the caller.
    /// </summary>
    /// <typeparam name="T">The result type of the operation.</typeparam>
    /// <param name="operation">The operation to run.</param>
    /// <param name="cancellationToken">Cancels the operation and the pauses between attempts.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsInUse(ex) && attempt < (ex is UnauthorizedAccessException ? MaxAttemptsWhenDenied : MaxAttempts))
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tells whether an exception means the file is open in another process. Windows reports that as a sharing or lock
    /// violation when opening the file, but as "access denied" when replacing or deleting it; on other systems an
    /// <see cref="UnauthorizedAccessException"/> is a real permission problem and is not retried.
    /// </summary>
    /// <param name="exception">The exception to inspect.</param>
    /// <returns>True if trying again shortly may succeed.</returns>
    internal static bool IsInUse(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        return exception is IOException io && (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;
    }
}
