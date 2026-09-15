namespace GitHubReleaseUpdater.Exceptions;

/// <summary>Raised when a downloaded file's hash does not match the expected value.</summary>
public sealed class ChecksumMismatchException : UpdaterException
{
    /// <summary>Path of the file that failed verification (already deleted by the time this is thrown).</summary>
    public string FilePath { get; }
    /// <summary>Expected hex-encoded hash.</summary>
    public string Expected { get; }
    /// <summary>Actual hex-encoded hash.</summary>
    public string Actual { get; }

    /// <inheritdoc />
    public ChecksumMismatchException() : this(string.Empty, string.Empty, string.Empty) { }
    /// <inheritdoc />
    public ChecksumMismatchException(string message) : base(message) { FilePath = Expected = Actual = string.Empty; }
    /// <inheritdoc />
    public ChecksumMismatchException(string message, Exception? innerException) : base(message, innerException) { FilePath = Expected = Actual = string.Empty; }

    /// <summary>Creates a new instance.</summary>
    public ChecksumMismatchException(string filePath, string expected, string actual)
        : base($"Checksum mismatch for '{filePath}': expected {expected}, got {actual}.")
    {
        FilePath = filePath;
        Expected = expected;
        Actual = actual;
    }
}
