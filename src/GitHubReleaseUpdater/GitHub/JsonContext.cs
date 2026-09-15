using System.Text.Json;
using System.Text.Json.Serialization;
using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.GitHub;

/// <summary>Source-generated JSON context for GitHub API payloads (trimming/AOT friendly).</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(GitHubErrorResponse))]
internal sealed partial class JsonContext : JsonSerializerContext
{
}

/// <summary>Minimal shape of a GitHub API error body.</summary>
internal sealed class GitHubErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("documentation_url")]
    public string? DocumentationUrl { get; init; }
}
