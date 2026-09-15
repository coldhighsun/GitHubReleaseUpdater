using System.Text.RegularExpressions;
using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater.Assets;

/// <summary>
/// Selects an asset by a file-name pattern. Supports <c>*</c> / <c>?</c> wildcards and the placeholders
/// <c>{os}</c>, <c>{arch}</c>, <c>{rid}</c> (from <see cref="RuntimeInfo"/>), <c>{version}</c> (the release's semantic version, e.g. <c>1.2.3</c>)
/// and <c>{tag}</c> (the raw tag name). Matching is case-insensitive; the first asset in release order wins.
/// </summary>
public sealed class PatternAssetSelector : IAssetSelector
{
    private readonly string _pattern;
    private readonly RuntimeInfo _runtime;
    private readonly string? _tagPrefix;

    /// <summary>Creates a selector.</summary>
    /// <param name="pattern">e.g. <c>myapp-{rid}.zip</c>, <c>myapp-*-{os}-{arch}.tar.gz</c>, <c>myapp-{version}-setup.exe</c>.</param>
    /// <param name="runtime">Runtime for the <c>{os}</c>/<c>{arch}</c>/<c>{rid}</c> placeholders; defaults to the current process.</param>
    /// <param name="tagPrefix">Tag prefix to strip when resolving <c>{version}</c>.</param>
    public PatternAssetSelector(string pattern, RuntimeInfo? runtime = null, string? tagPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _pattern = pattern;
        _runtime = runtime ?? RuntimeInfo.Current;
        _tagPrefix = tagPrefix;
    }

    /// <summary>The configured pattern.</summary>
    public string Pattern => _pattern;

    /// <inheritdoc />
    public GitHubAsset? Select(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var regex = BuildRegex(release);
        return release.Assets.FirstOrDefault(a => regex.IsMatch(a.Name));
    }

    private Regex BuildRegex(GitHubRelease release)
    {
        var version = SemanticVersion.TryParse(release.TagName, out var v, _tagPrefix) ? v.ToString() : release.TagName;
        var expanded = _pattern
            .Replace("{os}", _runtime.Os, StringComparison.OrdinalIgnoreCase)
            .Replace("{arch}", _runtime.Arch, StringComparison.OrdinalIgnoreCase)
            .Replace("{rid}", _runtime.Rid, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", version, StringComparison.OrdinalIgnoreCase)
            .Replace("{tag}", release.TagName, StringComparison.OrdinalIgnoreCase);

        var regexText = "^" + Regex.Escape(expanded).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return new Regex(regexText, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
