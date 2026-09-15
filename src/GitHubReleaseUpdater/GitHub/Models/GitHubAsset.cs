using System.Text.Json.Serialization;

namespace GitHubReleaseUpdater.GitHub.Models;

/// <summary>A downloadable file attached to a GitHub release.</summary>
public sealed class GitHubAsset
{
    /// <summary>Asset id, used for authenticated downloads via the API.</summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>File name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional label shown on the release page.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>MIME type reported by GitHub.</summary>
    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    /// <summary>Size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Public download URL (works without auth for public repositories).</summary>
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = string.Empty;

    /// <summary>API URL of the asset; download with <c>Accept: application/octet-stream</c>.</summary>
    [JsonPropertyName("url")]
    public string ApiUrl { get; init; } = string.Empty;

    /// <summary>Content digest reported by GitHub, e.g. <c>sha256:abcd…</c>. May be null on older assets or GHE.</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }

    /// <summary>Upload timestamp.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <inheritdoc />
    public override string ToString() => Name;
}
