using System.Text.Json.Serialization;

namespace GitHubReleaseUpdater.GitHub.Models;

/// <summary>
/// A GitHub release.
/// </summary>
public sealed class GitHubRelease
{
    /// <summary>
    /// Release id.
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>
    /// Git tag the release points at, e.g. <c>v1.2.3</c>.
    /// </summary>
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = string.Empty;

    /// <summary>
    /// Release title.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Release notes (Markdown).
    /// </summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }

    /// <summary>
    /// True for a draft release.
    /// </summary>
    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    /// <summary>
    /// True for a pre-release.
    /// </summary>
    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    /// <summary>
    /// Release page on github.com.
    /// </summary>
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    /// <summary>
    /// Publish timestamp (null for drafts).
    /// </summary>
    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>
    /// Creation timestamp.
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Attached assets.
    /// </summary>
    [JsonPropertyName("assets")]
    public IReadOnlyList<GitHubAsset> Assets { get; init; } = [];

    /// <inheritdoc />
    public override string ToString() => TagName;
}
