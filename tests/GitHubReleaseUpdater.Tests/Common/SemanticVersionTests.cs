using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", "")]
    [InlineData("v1.2.3", 1, 2, 3, "", "")]
    [InlineData("V10.0.1", 10, 0, 1, "", "")]
    [InlineData("1.2", 1, 2, 0, "", "")]
    [InlineData("7", 7, 0, 0, "", "")]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1", "")]
    [InlineData("1.2.3+build.5", 1, 2, 3, "", "build.5")]
    [InlineData("1.2.3-rc.1+sha.abc", 1, 2, 3, "rc.1", "sha.abc")]
    [InlineData("  v1.0.0 ", 1, 0, 0, "", "")]
    public void Parses_common_forms(string input, int major, int minor, int patch, string pre, string build)
    {
        var v = SemanticVersion.Parse(input);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(pre, v.Prerelease);
        Assert.Equal(build, v.BuildMetadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    [InlineData("nightly-2026-09-01")]
    public void Rejects_invalid(string input)
    {
        Assert.False(SemanticVersion.TryParse(input, out _));
        Assert.Throws<VersionParseException>(() => SemanticVersion.Parse(input));
    }

    [Fact]
    public void Strips_custom_tag_prefix()
    {
        var v = SemanticVersion.Parse("release-1.4.0", tagPrefix: "release-");
        Assert.Equal(new SemanticVersion(1, 4, 0), v);
        Assert.False(SemanticVersion.TryParse("release-1.4.0", out _));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void Orders_per_semver_spec(string lower, string higher)
    {
        SemanticVersion a = lower, b = higher;
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a.CompareTo(b) < 0);
    }

    /// <summary>
    /// Numeric prerelease identifiers beyond <see cref="int"/> (and <see cref="long"/>) range, such as CI timestamps,
    /// must still compare numerically rather than falling back to ordinal string comparison.
    /// </summary>
    [Theory]
    [InlineData("1.0.0-ci.3000000000", "1.0.0-ci.20240924120000")]
    [InlineData("1.0.0-ci.9", "1.0.0-ci.2147483648")]
    [InlineData("1.0.0-ci.99999999999999999999", "1.0.0-ci.100000000000000000000")]
    [InlineData("1.0.0-ci.20240924120000", "1.0.0-ci.alpha")]
    public void CompareTo_numeric_prerelease_identifier_exceeds_int_range_compares_numerically(string lower, string higher)
    {
        SemanticVersion a = lower, b = higher;

        var result = a.CompareTo(b);

        Assert.True(result < 0);
        Assert.True(b.CompareTo(a) > 0);
    }

    /// <summary>
    /// A numeric prerelease identifier with a leading zero is invalid per SemVer, so the constructor must reject it
    /// rather than silently accepting a value that could never be produced by <see cref="SemanticVersion.Parse"/>.
    /// </summary>
    [Fact]
    public void Constructor_rejects_prerelease_numeric_identifier_with_leading_zero()
    {
        Assert.Throws<ArgumentException>(() => new SemanticVersion(1, 0, 0, "beta.01"));
    }

    /// <summary>
    /// Versions that compare equal because a numeric prerelease identifier is written with different (but both
    /// valid, i.e. non-leading-zero) casing of its sibling alphanumeric identifier must also hash equal, or
    /// hash-based collections would treat them as distinct. Ordinal comparison means casing differences are NOT
    /// expected to compare equal here — this instead pins down that two references built from the same valid
    /// identifier chain via different call sites hash identically.
    /// </summary>
    [Fact]
    public void GetHashCode_equal_versions_hash_equal()
    {
        var a = new SemanticVersion(1, 0, 0, "beta.1");
        var b = SemanticVersion.Parse("1.0.0-beta.1");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Build_metadata_is_ignored_for_equality()
    {
        Assert.Equal(SemanticVersion.Parse("1.0.0+a"), SemanticVersion.Parse("1.0.0+b"));
        Assert.Equal(SemanticVersion.Parse("1.0.0+a").GetHashCode(), SemanticVersion.Parse("1.0.0+b").GetHashCode());
    }

    [Fact]
    public void ToString_round_trips()
    {
        Assert.Equal("1.2.3-beta.1+build.5", SemanticVersion.Parse("v1.2.3-beta.1+build.5").ToString());
        Assert.Equal("1.2.0", SemanticVersion.Parse("1.2").ToString());
    }

    [Fact]
    public void FromVersion_drops_revision_and_clamps_missing_parts()
    {
        Assert.Equal(new SemanticVersion(1, 2, 3), SemanticVersion.FromVersion(new Version(1, 2, 3, 4)));
        Assert.Equal(new SemanticVersion(1, 2, 0), SemanticVersion.FromVersion(new Version(1, 2)));
    }

    [Fact]
    public void Comparison_operators_cover_not_equal_and_or_equal_cases()
    {
        SemanticVersion a = "1.0.0";
        SemanticVersion b = "1.0.0";
        SemanticVersion c = "2.0.0";

        Assert.False(a != b);
        Assert.True(a != c);
        Assert.True(a <= b);
        Assert.True(a >= b);
        Assert.True(a <= c);
        Assert.False(c <= a);
        Assert.True(c >= a);
        Assert.False(a >= c);
    }

    [Fact]
    public void Equals_object_overload_handles_matching_type_and_mismatches()
    {
        SemanticVersion a = "1.0.0";
        object same = (SemanticVersion)"1.0.0";
        object different = (SemanticVersion)"2.0.0";

        object unrelated = "1.0.0";

        Assert.True(a.Equals(same));
        Assert.False(a.Equals(different));
        Assert.False(a.Equals(unrelated));
        Assert.False(a.Equals(null));
    }
}
