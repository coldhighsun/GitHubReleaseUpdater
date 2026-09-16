using System.Security.Cryptography;

namespace GitHubReleaseUpdater.Verification;

/// <summary>
/// Computes file hashes without loading the whole file into memory.
/// </summary>
public static class FileHasher
{
    /// <summary>
    /// Computes the SHA-256 hash of a file and returns it as lowercase hex.
    /// </summary>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Case-insensitive comparison of two hex digests, ignoring surrounding whitespace.
    /// </summary>
    public static bool HashEquals(string? expected, string? actual)
        => expected is not null && actual is not null
           && expected.Trim().Equals(actual.Trim(), StringComparison.OrdinalIgnoreCase);
}
