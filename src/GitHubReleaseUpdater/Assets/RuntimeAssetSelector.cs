using System.Text.RegularExpressions;
using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Assets;

/// <summary>
/// Picks the asset whose file name best matches the current (or given) OS and architecture,
/// tolerating common naming conventions such as <c>app-win-x64.zip</c>, <c>app_linux_amd64.tar.gz</c>, <c>app-darwin-arm64.dmg</c>.
/// Checksum/signature files are never selected.
/// </summary>
public sealed partial class RuntimeAssetSelector : IAssetSelector
{
    /// <summary>
    /// Target OS/architecture to match asset names against.
    /// </summary>
    private readonly RuntimeInfo _runtime;

    /// <summary>
    /// Extensions that break ties between otherwise equally scored assets; earlier entries win.
    /// </summary>
    private readonly IReadOnlyCollection<string> _preferredExtensions;

    /// <summary>
    /// Creates a selector for the current runtime.
    /// </summary>
    public RuntimeAssetSelector() : this(RuntimeInfo.Current) { }

    /// <summary>
    /// Creates a selector for a specific runtime.
    /// </summary>
    /// <param name="runtime">Target OS/arch.</param>
    /// <param name="preferredExtensions">Optional extensions (e.g. <c>.zip</c>, <c>.msi</c>) that break ties; earlier entries win.</param>
    public RuntimeAssetSelector(RuntimeInfo runtime, params string[] preferredExtensions)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(preferredExtensions);
        if (preferredExtensions.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Preferred extensions must not be null or empty.", nameof(preferredExtensions));
        }
        _runtime = runtime;
        // Lowercased because Score matches them against the lowercased asset name.
        _preferredExtensions = preferredExtensions
            .Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant())
            .ToArray();
    }

    /// <inheritdoc />
    public GitHubAsset? Select(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var osTokens = RuntimeInfo.OsAliases.TryGetValue(_runtime.Os, out var o) ? o : [_runtime.Os];
        var archTokens = RuntimeInfo.ArchAliases.TryGetValue(_runtime.Arch, out var a) ? a : [_runtime.Arch];

        GitHubAsset? best = null;
        var bestScore = 0;
        foreach (var asset in release.Assets)
        {
            if (IsMetadataFile(asset.Name)) continue;
            var score = Score(asset.Name, osTokens, archTokens);
            if (score > bestScore)
            {
                best = asset;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// Scores an asset's file name by how well it matches the target OS/architecture and preferred extensions;
    /// returns 0 when the name explicitly names a different OS or architecture, or matches neither.
    /// </summary>
    private int Score(string name, string[] osTokens, string[] archTokens)
    {
        var tokens = TokenizeName(name);
        var osHit = tokens.Any(t => osTokens.Contains(t, StringComparer.OrdinalIgnoreCase));
        var archHit = tokens.Any(t => archTokens.Contains(t, StringComparer.OrdinalIgnoreCase));

        // Reject files that explicitly name a *different* OS or arch.
        var otherOs = RuntimeInfo.OsAliases.Where(kv => !kv.Key.Equals(_runtime.Os, StringComparison.OrdinalIgnoreCase))
                                            .SelectMany(kv => kv.Value);
        var otherArch = RuntimeInfo.ArchAliases.Where(kv => !kv.Key.Equals(_runtime.Arch, StringComparison.OrdinalIgnoreCase))
                                                .SelectMany(kv => kv.Value);
        if (!osHit && tokens.Any(t => otherOs.Contains(t, StringComparer.OrdinalIgnoreCase))) return 0;
        if (!archHit && tokens.Any(t => otherArch.Contains(t, StringComparer.OrdinalIgnoreCase))) return 0;

        var score = 0;
        if (osHit) score += 100;
        if (archHit) score += 50;
        if (!osHit && !archHit) return 0;

        // Extension preference (earlier = better).
        var lower = name.ToLowerInvariant();
        var idx = 0;
        foreach (var ext in _preferredExtensions)
        {
            if (lower.EndsWith(ext, StringComparison.Ordinal))
            {
                score += 20 - Math.Min(idx, 19);
                break;
            }
            idx++;
        }

        // Windows heuristics: prefer installers/archives over bare exe when nothing else distinguishes them.
        if (_runtime.Os.Equals("win", StringComparison.OrdinalIgnoreCase) && (lower.EndsWith(".msi", StringComparison.Ordinal) || lower.EndsWith(".zip", StringComparison.Ordinal))) score += 1;
        return score;
    }

    /// <summary>
    /// Lowercases and splits a file name into tokens, normalizing common compound OS/arch aliases first.
    /// </summary>
    private static string[] TokenizeName(string name)
    {
        // Collapse compound aliases that contain separators so they survive tokenization.
        var normalized = name.ToLowerInvariant()
            .Replace("x86_64", "x64", StringComparison.Ordinal)
            .Replace("x86-64", "x64", StringComparison.Ordinal)
            .Replace("64-bit", "64bit", StringComparison.Ordinal)
            .Replace("32-bit", "32bit", StringComparison.Ordinal);
        return TokenSplit().Split(normalized).Where(t => t.Length > 0).ToArray();
    }

    /// <summary>
    /// True for checksum, signature and similar sidecar files. Matches only known metadata extensions and
    /// aggregate sums file names/suffixes (mirroring <see cref="Verification.ReleaseChecksumProvider.IsAggregate"/>),
    /// not an arbitrary substring, so a real asset whose name happens to contain a word like "checksums" is not excluded.
    /// </summary>
    public static bool IsMetadataFile(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.EndsWith(".sha256", StringComparison.Ordinal)
            || lower.EndsWith(".sha512", StringComparison.Ordinal)
            || lower.EndsWith(".sha1", StringComparison.Ordinal)
            || lower.EndsWith(".md5", StringComparison.Ordinal)
            || lower.EndsWith(".sig", StringComparison.Ordinal)
            || lower.EndsWith(".asc", StringComparison.Ordinal)
            || lower.EndsWith(".pem", StringComparison.Ordinal)
            || lower.EndsWith(".sbom", StringComparison.Ordinal)
            || IsAggregateSumsFileName(lower);
    }

    /// <summary>
    /// True when the (already lowercased) name is an exact known aggregate sums file name, or ends with one of
    /// their common suffixed forms (e.g. <c>myapp_1.2.3_checksums.txt</c>).
    /// </summary>
    private static bool IsAggregateSumsFileName(string lower)
    {
        if (lower is "sha256sums" or "sha256sums.txt" or "sha256sum.txt" or "sha256sum" or "checksums.txt" or "checksums" or "checksums.sha256" or "sha256.txt" or "sha512sums" or "sha512sums.txt")
            return true;
        return lower.EndsWith("checksums.txt", StringComparison.Ordinal)
            || lower.EndsWith("sha256sums.txt", StringComparison.Ordinal)
            || lower.EndsWith("sha256sums", StringComparison.Ordinal)
            || lower.EndsWith("sha512sums.txt", StringComparison.Ordinal)
            || lower.EndsWith("sha512sums", StringComparison.Ordinal);
    }

    /// <summary>
    /// Regex that splits a file name into tokens on separator characters.
    /// </summary>
    [GeneratedRegex(@"[-_.\s()\[\]]+")]
    private static partial Regex TokenSplit();
}
