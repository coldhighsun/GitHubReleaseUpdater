using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Assets;

/// <summary>
/// Picks the asset whose file name best matches the current (or given) OS and architecture,
/// tolerating common naming conventions such as <c>app-win-x64.zip</c>, <c>app_linux_amd64.tar.gz</c>, <c>app-darwin-arm64.dmg</c>.
/// Checksum/signature files are never selected.
/// </summary>
public sealed class RuntimeAssetSelector : IAssetSelector
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
    /// When true, an asset whose file name names neither an OS nor an architecture (for example <c>MyApp-Setup.exe</c>)
    /// may be chosen if no asset matches the runtime; it ranks below every asset that does. Only files that look like
    /// something to install on the target OS are considered (<c>.exe</c>, <c>.msi</c>, <c>.msix</c>, <c>.appx</c> on
    /// Windows; <c>.dmg</c>, <c>.pkg</c> on macOS; <c>.deb</c>, <c>.rpm</c>, <c>.appimage</c> on Linux) plus any
    /// extension passed as a preferred extension, so notes, symbols or source archives are never picked. Assets that
    /// name a different OS or architecture and sidecar files are still never chosen. Defaults to false, so a name is
    /// never guessed unless the caller opts in; use it for releases that publish a single installer for one platform.
    /// </summary>
    public bool AllowUnknownPlatform { get; init; }

    /// <summary>
    /// Extensions of things to install on Windows.
    /// </summary>
    private static readonly string[] InstallableOnWindows = [".exe", ".msi", ".msix", ".appx"];

    /// <summary>
    /// Extensions of things to install on macOS.
    /// </summary>
    private static readonly string[] InstallableOnMac = [".dmg", ".pkg"];

    /// <summary>
    /// Extensions of things to install on Linux.
    /// </summary>
    private static readonly string[] InstallableOnLinux = [".deb", ".rpm", ".appimage"];

    /// <summary>
    /// Lowercased extensions a platform-less file name may have: those of the target OS plus the preferred ones.
    /// </summary>
    private readonly string[] _installableExtensions;

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
        _installableExtensions = (_runtime.Os.ToLowerInvariant() switch
        {
            "win" => InstallableOnWindows,
            "osx" => InstallableOnMac,
            "linux" => InstallableOnLinux,
            _ => [],
        }).Concat(_preferredExtensions).ToArray();
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
        var tokens = AssetPlatformDetector.TokenizeName(name);
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
        var unknownPlatform = !osHit && !archHit;
        if (unknownPlatform)
        {
            if (!AllowUnknownPlatform || !AssetPlatformDetector.Detect(name).IsUnknown || !LooksInstallable(name))
            {
                return 0;
            }

            score = 1;
        }

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
    /// Whether a platform-less file name has an extension that is something to install on the target OS or one the caller
    /// asked for.
    /// </summary>
    /// <param name="name">The asset file name.</param>
    /// <returns>True if the file may be chosen as a platform-less installer.</returns>
    private bool LooksInstallable(string name)
    {
        return _installableExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for checksum, signature and similar sidecar files. Matches only known metadata extensions and
    /// aggregate sums file names/suffixes (mirroring <see cref="Verification.ReleaseChecksumProvider.IsAggregate"/>),
    /// not an arbitrary substring, so a real asset whose name happens to contain a word like "checksums" is not excluded.
    /// </summary>
    public static bool IsMetadataFile(string name)
    {
        var lower = name.ToLowerInvariant();
        return MetadataSuffixes.Any(s => lower.EndsWith(s, StringComparison.Ordinal))
            || IsAggregateSumsFileName(lower);
    }

    /// <summary>
    /// Lowercase suffixes of checksum, signature, attestation and SBOM sidecar files. These are matched as whole
    /// compound suffixes (e.g. <c>.sbom.json</c>, not <c>.json</c>) so ordinary JSON release assets stay selectable.
    /// </summary>
    private static readonly string[] MetadataSuffixes =
    [
        ".sha256", ".sha512", ".sha1", ".md5", ".sha256sum", ".sha512sum",
        ".sig", ".asc", ".pem", ".minisig", ".p7s", ".sigstore", ".sigstore.json", ".intoto.jsonl",
        ".sbom", ".sbom.json", ".spdx", ".spdx.json", ".cdx.json",
    ];

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
}
