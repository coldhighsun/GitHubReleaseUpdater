using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace GitHubReleaseUpdater.Assets;

/// <summary>
/// Selects an asset by a file-name pattern. Supports <c>*</c> / <c>?</c> wildcards and the placeholders
/// <c>{os}</c>, <c>{arch}</c>, <c>{rid}</c> (from <see cref="RuntimeInfo"/>), <c>{version}</c> (the release's normalized
/// semantic version, e.g. <c>1.2.0</c> for tag <c>v1.2</c>), <c>{tagversion}</c> (the tag's version text as written,
/// with the tag prefix and a leading <c>v</c> removed but not normalized, e.g. <c>1.2</c> for tag <c>v1.2</c>) and
/// <c>{tag}</c> (the raw tag name). Matching is case-insensitive; the first asset in release order wins.
/// </summary>
public sealed class PatternAssetSelector : IAssetSelector
{
    /// <summary>
    /// Matches a placeholder or a <c>*</c>/<c>?</c> wildcard in the configured pattern.
    /// </summary>
    private static readonly Regex PatternToken = new(@"\{(?:os|arch|rid|version|tagversion|tag)\}|[*?]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The configured file-name pattern, with placeholders not yet expanded.
    /// </summary>
    private readonly string _pattern;

    /// <summary>
    /// Runtime used to resolve the <c>{os}</c>/<c>{arch}</c>/<c>{rid}</c> placeholders.
    /// </summary>
    private readonly RuntimeInfo _runtime;

    /// <summary>
    /// Tag prefix to strip when resolving <c>{version}</c> and <c>{tagversion}</c>.
    /// </summary>
    private readonly string? _tagPrefix;

    /// <summary>
    /// Creates a selector.
    /// </summary>
    /// <param name="pattern">e.g. <c>myapp-{rid}.zip</c>, <c>myapp-*-{os}-{arch}.tar.gz</c>, <c>myapp-{version}-setup.exe</c>.</param>
    /// <param name="runtime">Runtime for the <c>{os}</c>/<c>{arch}</c>/<c>{rid}</c> placeholders; defaults to the current process.</param>
    /// <param name="tagPrefix">Tag prefix to strip when resolving <c>{version}</c> and <c>{tagversion}</c>.</param>
    public PatternAssetSelector(string pattern, RuntimeInfo? runtime = null, string? tagPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _pattern = pattern;
        _runtime = runtime ?? RuntimeInfo.Current;
        _tagPrefix = tagPrefix;
    }

    /// <summary>
    /// The configured pattern.
    /// </summary>
    public string Pattern => _pattern;

    /// <inheritdoc />
    public GitHubAsset? Select(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var regex = BuildRegex(release);
        return release.Assets.FirstOrDefault(a => regex.IsMatch(a.Name));
    }

    /// <summary>
    /// Expands the pattern's placeholders for <paramref name="release"/> and compiles it into a wildcard-matching regex.
    /// </summary>
    private Regex BuildRegex(GitHubRelease release)
    {
        var version = SemanticVersion.TryParse(release.TagName, out var v, _tagPrefix) ? v.ToString() : release.TagName;

        // Expand placeholders and wildcards in a single pass over the original pattern, escaping substituted values,
        // so text inside a tag (e.g. '*' or "{tag}") matches literally instead of acting as a wildcard or being
        // expanded again.
        var regexText = new StringBuilder("^");
        var last = 0;
        foreach (Match m in PatternToken.Matches(_pattern))
        {
            regexText.Append(Regex.Escape(_pattern[last..m.Index]));
            regexText.Append(m.Value.ToLowerInvariant() switch
            {
                "*" => ".*",
                "?" => ".",
                "{os}" => Regex.Escape(_runtime.Os),
                "{arch}" => Regex.Escape(_runtime.Arch),
                "{rid}" => Regex.Escape(_runtime.Rid),
                "{version}" => Regex.Escape(version),
                "{tagversion}" => Regex.Escape(SemanticVersion.StripTagPrefix(release.TagName, _tagPrefix).ToString()),
                _ => Regex.Escape(release.TagName), // {tag}
            });
            last = m.Index + m.Length;
        }
        regexText.Append(Regex.Escape(_pattern[last..])).Append('$');

        return new Regex(regexText.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}