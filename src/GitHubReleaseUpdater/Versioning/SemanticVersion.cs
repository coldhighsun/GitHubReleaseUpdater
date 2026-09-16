using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace GitHubReleaseUpdater.Versioning;

/// <summary>
/// A minimal Semantic Versioning 2.0.0 implementation with lenient parsing of common release tag styles
/// (e.g. <c>v1.2.3</c>, <c>1.2</c>, <c>release-1.2.3</c>).
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    /// <summary>
    /// Major version.
    /// </summary>
    public int Major { get; }
    /// <summary>
    /// Minor version.
    /// </summary>
    public int Minor { get; }
    /// <summary>
    /// Patch version.
    /// </summary>
    public int Patch { get; }
    /// <summary>
    /// Pre-release identifiers, e.g. <c>beta.1</c>. Empty for a stable release.
    /// </summary>
    public string Prerelease { get; }
    /// <summary>
    /// Build metadata (ignored when comparing).
    /// </summary>
    public string BuildMetadata { get; }

    /// <summary>
    /// True when <see cref="Prerelease"/> is non-empty.
    /// </summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>
    /// Creates a version from its components.
    /// </summary>
    public SemanticVersion(int major, int minor, int patch, string? prerelease = null, string? buildMetadata = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? string.Empty;
        BuildMetadata = buildMetadata ?? string.Empty;
    }

    /// <summary>
    /// Parses a version string, throwing <see cref="VersionParseException"/> on failure.
    /// </summary>
    /// <param name="input">Version text such as <c>v1.2.3-beta.1+build.5</c>.</param>
    /// <param name="tagPrefix">Optional prefix to strip before parsing (a leading <c>v</c>/<c>V</c> is always accepted).</param>
    public static SemanticVersion Parse(string input, string? tagPrefix = null)
        => TryParse(input, out var v, tagPrefix) ? v : throw new VersionParseException(input);

    /// <summary>
    /// Attempts to parse a version string.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? input, [NotNullWhen(true)] out SemanticVersion? version, string? tagPrefix = null)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = input.AsSpan().Trim();
        if (!string.IsNullOrEmpty(tagPrefix) && s.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
            s = s[tagPrefix.Length..];
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];
        if (s.IsEmpty) return false;

        string? build = null;
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            build = s[(plus + 1)..].ToString();
            if (build.Length == 0 || !IsValidIdentifierChain(build, allowLeadingZeros: true)) return false;
            s = s[..plus];
        }

        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..].ToString();
            if (pre.Length == 0 || !IsValidIdentifierChain(pre, allowLeadingZeros: false)) return false;
            s = s[..dash];
        }

        // Core: "1", "1.2" or "1.2.3" (missing components default to 0).
        Span<int> parts = stackalloc int[3];
        var count = 0;
        while (true)
        {
            if (count == 3) return false;
            var dot = s.IndexOf('.');
            var piece = dot < 0 ? s : s[..dot];
            if (!TryParseNumeric(piece, out parts[count])) return false;
            count++;
            if (dot < 0) break;
            s = s[(dot + 1)..];
        }

        version = new SemanticVersion(parts[0], parts[1], parts[2], pre, build);
        return true;
    }

    /// <summary>
    /// Parses a major/minor/patch numeric piece, rejecting empty, oversized or leading-zero values.
    /// </summary>
    private static bool TryParseNumeric(ReadOnlySpan<char> piece, out int value)
    {
        value = 0;
        if (piece.IsEmpty || piece.Length > 9) return false;
        if (piece.Length > 1 && piece[0] == '0') return false;
        foreach (var c in piece)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }
        return int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Validates a dot-separated identifier chain (prerelease or build metadata) per semver's character
    /// and leading-zero rules.
    /// </summary>
    private static bool IsValidIdentifierChain(string chain, bool allowLeadingZeros)
    {
        foreach (var id in chain.Split('.'))
        {
            if (id.Length == 0) return false;
            var allDigits = true;
            foreach (var c in id)
            {
                if (char.IsAsciiDigit(c)) continue;
                allDigits = false;
                if (!char.IsAsciiLetter(c) && c != '-') return false;
            }
            if (!allowLeadingZeros && allDigits && id.Length > 1 && id[0] == '0') return false;
        }
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>
    /// Compares two prerelease identifier strings per semver precedence rules (numeric identifiers compare
    /// numerically and rank lower than alphanumeric ones; a stable version outranks any prerelease).
    /// </summary>
    private static int ComparePrerelease(string a, string b)
    {
        // A stable version has higher precedence than a pre-release of the same core.
        if (a.Length == 0) return b.Length == 0 ? 0 : 1;
        if (b.Length == 0) return -1;

        var ia = a.Split('.');
        var ib = b.Split('.');
        var n = Math.Min(ia.Length, ib.Length);
        for (var i = 0; i < n; i++)
        {
            var na = int.TryParse(ia[i], NumberStyles.None, CultureInfo.InvariantCulture, out var va);
            var nb = int.TryParse(ib[i], NumberStyles.None, CultureInfo.InvariantCulture, out var vb);
            int c;
            if (na && nb) c = va.CompareTo(vb);
            else if (na) c = -1;              // numeric identifiers have lower precedence than alphanumeric
            else if (nb) c = 1;
            else c = string.CompareOrdinal(ia[i], ib[i]);
            if (c != 0) return c;
        }
        return ia.Length.CompareTo(ib.Length);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SemanticVersion);
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    /// <inheritdoc />
    public override string ToString()
    {
        var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (Prerelease.Length > 0) core += "-" + Prerelease;
        if (BuildMetadata.Length > 0) core += "+" + BuildMetadata;
        return core;
    }

    /// <summary>
    /// Implicit conversion from string using <see cref="Parse(string, string?)"/>.
    /// </summary>
    public static implicit operator SemanticVersion(string value) => Parse(value);

    /// <summary>
    /// Converts a <see cref="Version"/> (e.g. from an assembly) to a semantic version, dropping the revision.
    /// </summary>
    public static SemanticVersion FromVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new SemanticVersion(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }

#pragma warning disable CS1591
    public static bool operator ==(SemanticVersion? a, SemanticVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemanticVersion? a, SemanticVersion? b) => !(a == b);
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
#pragma warning restore CS1591
}
