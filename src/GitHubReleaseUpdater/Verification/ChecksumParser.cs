namespace GitHubReleaseUpdater.Verification;

/// <summary>Parses checksum files and digest strings.</summary>
public static class ChecksumParser
{
    private const int Sha256HexLength = 64;

    /// <summary>
    /// Normalizes a digest such as <c>sha256:ABCD…</c> or <c>abcd…</c> to lowercase hex, or returns null when it is not a SHA-256.
    /// </summary>
    public static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var s = digest.Trim();
        var colon = s.IndexOf(':');
        if (colon >= 0)
        {
            if (!s.AsSpan(0, colon).Equals("sha256", StringComparison.OrdinalIgnoreCase)) return null;
            s = s[(colon + 1)..];
        }
        return IsHex(s, Sha256HexLength) ? s.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Parses the content of a <c>SHA256SUMS</c>-style file. Supported line formats:
    /// <list type="bullet">
    /// <item><c>&lt;hash&gt;  &lt;file&gt;</c> (GNU coreutils, text mode)</item>
    /// <item><c>&lt;hash&gt; *&lt;file&gt;</c> (binary mode)</item>
    /// <item><c>SHA256 (&lt;file&gt;) = &lt;hash&gt;</c> (BSD)</item>
    /// <item><c>&lt;hash&gt;</c> alone (a single-file <c>.sha256</c> sidecar)</item>
    /// </list>
    /// Returns a map of file name (path stripped) → lowercase hash.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content)) return result;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // BSD: SHA256 (file) = hash
            if (line.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase) && line.Contains('(') && line.Contains(") ="))
            {
                var open = line.IndexOf('(');
                var close = line.LastIndexOf(')');
                var eq = line.LastIndexOf('=');
                if (open >= 0 && close > open && eq > close)
                {
                    var f = line[(open + 1)..close].Trim();
                    var h = line[(eq + 1)..].Trim();
                    if (IsHex(h, Sha256HexLength)) result[FileNameOnly(f)] = h.ToLowerInvariant();
                }
                continue;
            }

            var sep = line.IndexOfAny([' ', '\t']);
            if (sep < 0)
            {
                // A bare hash: sidecar file with a single digest.
                if (IsHex(line, Sha256HexLength)) result[string.Empty] = line.ToLowerInvariant();
                continue;
            }

            var hash = line[..sep];
            if (!IsHex(hash, Sha256HexLength)) continue;
            var file = line[sep..].TrimStart(' ', '\t', '*');
            if (file.Length == 0) result[string.Empty] = hash.ToLowerInvariant();
            else result[FileNameOnly(file)] = hash.ToLowerInvariant();
        }
        return result;
    }

    /// <summary>Looks up the digest for <paramref name="fileName"/> in a parsed sums map; falls back to a lone bare hash if present.</summary>
    public static string? FindFor(IReadOnlyDictionary<string, string> sums, string fileName)
    {
        if (sums.TryGetValue(fileName, out var h)) return h;
        if (sums.Count == 1 && sums.TryGetValue(string.Empty, out h)) return h;
        return null;
    }

    private static string FileNameOnly(string path)
    {
        var i = path.LastIndexOfAny(['/', '\\']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static bool IsHex(ReadOnlySpan<char> s, int length)
    {
        if (s.Length != length) return false;
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
