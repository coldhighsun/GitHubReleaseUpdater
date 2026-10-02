using System.Text.RegularExpressions;
using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Assets;

/// <summary>
/// Detects the OS and architecture an asset file name targets, understanding the aliases listed in
/// <see cref="RuntimeInfo.OsAliases"/> and <see cref="RuntimeInfo.ArchAliases"/> (e.g. <c>windows</c>, <c>darwin</c>,
/// <c>amd64</c>, <c>aarch64</c>). <see cref="RuntimeAssetSelector"/> uses the same tokenization.
/// </summary>
public static partial class AssetPlatformDetector
{
    /// <summary>
    /// Detects the platform named by <paramref name="assetName"/>; a part the name does not mention is null.
    /// </summary>
    public static AssetPlatform Detect(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        var tokens = TokenizeName(assetName);
        return new AssetPlatform(FirstMatch(RuntimeInfo.OsAliases, tokens), FirstMatch(RuntimeInfo.ArchAliases, tokens));
    }

    /// <summary>
    /// Describes every asset of <paramref name="release"/>: its detected platform, whether it is a metadata file,
    /// and whether it matches <paramref name="current"/> (the current process when null). Metadata files are
    /// included, flagged by <see cref="DescribedAsset.IsMetadata"/>.
    /// </summary>
    public static IReadOnlyList<DescribedAsset> Describe(GitHubRelease release, RuntimeInfo? current = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        current ??= RuntimeInfo.Current;
        return release.Assets.Select(a => Describe(a, current)).ToArray();
    }

    /// <summary>
    /// Describes a single asset; see <see cref="Describe(GitHubRelease, RuntimeInfo?)"/>.
    /// </summary>
    private static DescribedAsset Describe(GitHubAsset asset, RuntimeInfo current)
    {
        var platform = Detect(asset.Name);
        var isMetadata = RuntimeAssetSelector.IsMetadataFile(asset.Name);
        var matches = !isMetadata
            && !platform.IsUnknown
            && (platform.Os is null || platform.Os.Equals(current.Os, StringComparison.OrdinalIgnoreCase))
            && (platform.Arch is null || platform.Arch.Equals(current.Arch, StringComparison.OrdinalIgnoreCase));
        return new DescribedAsset(asset, platform, isMetadata, matches);
    }

    /// <summary>
    /// Returns the canonical name whose alias list contains one of <paramref name="tokens"/>, or null.
    /// </summary>
    private static string? FirstMatch(IReadOnlyDictionary<string, string[]> aliases, string[] tokens)
    {
        foreach (var (canonical, names) in aliases)
        {
            if (tokens.Any(t => names.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                return canonical;
            }
        }
        return null;
    }

    /// <summary>
    /// Lowercases and splits a file name into tokens, normalizing common compound OS/arch aliases first.
    /// </summary>
    internal static string[] TokenizeName(string name)
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
    /// Regex that splits a file name into tokens on separator characters.
    /// </summary>
    [GeneratedRegex(@"[-_.\s()\[\]]+")]
    private static partial Regex TokenSplit();
}
