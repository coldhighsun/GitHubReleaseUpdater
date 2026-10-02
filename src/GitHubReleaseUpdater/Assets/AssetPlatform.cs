namespace GitHubReleaseUpdater.Assets;

/// <summary>
/// The OS and architecture an asset's file name says it targets. A part the name does not mention is null.
/// </summary>
/// <param name="Os">Canonical OS name (<c>win</c>, <c>osx</c>, <c>linux</c>, <c>freebsd</c>), or null when the name has none.</param>
/// <param name="Arch">Canonical architecture name (<c>x64</c>, <c>x86</c>, <c>arm64</c>, <c>arm</c>), or null when the name has none.</param>
public sealed record AssetPlatform(string? Os, string? Arch)
{
    /// <summary>
    /// True when the file name names neither an OS nor an architecture (for example <c>app-setup.exe</c>).
    /// </summary>
    public bool IsUnknown => Os is null && Arch is null;

    /// <summary>
    /// Runtime identifier in the <c>os-arch</c> form (e.g. <c>win-x64</c>); a missing part is left out, and
    /// <see cref="string.Empty"/> is returned when <see cref="IsUnknown"/>.
    /// </summary>
    public string Rid => Os is not null && Arch is not null ? $"{Os}-{Arch}" : Os ?? Arch ?? string.Empty;
}
