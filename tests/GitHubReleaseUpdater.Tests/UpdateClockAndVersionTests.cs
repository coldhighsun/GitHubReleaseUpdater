using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.LastCheck;
using GitHubReleaseUpdater.Versioning;
using System.Reflection;
using System.Reflection.Emit;

namespace GitHubReleaseUpdater.Tests;

/// <summary>
/// Tests for the injectable clock, the handling of a store that cannot write, the version helpers and the opt-in
/// fallback of <see cref="RuntimeAssetSelector"/>.
/// </summary>
public class UpdateClockAndVersionTests
{
    /// <summary>
    /// The runtime the selector tests target.
    /// </summary>
    private static readonly RuntimeInfo Win64 = new("win", "x64");

    /// <summary>
    /// Formatting without metadata drops the part after the plus sign.
    /// </summary>
    /// <param name="input">The version text.</param>
    /// <param name="expected">The text without build metadata.</param>
    [Theory]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("1.2.3-rc.1+abc123", "1.2.3-rc.1")]
    [InlineData("1.2.3", "1.2.3")]
    public void ToString_without_metadata_drops_the_build_metadata(string input, string expected)
    {
        Assert.Equal(expected, SemanticVersion.Parse(input).ToString(includeMetadata: false));
    }

    /// <summary>
    /// Formatting with metadata matches the plain form.
    /// </summary>
    [Fact]
    public void ToString_with_metadata_matches_the_plain_form()
    {
        var version = SemanticVersion.Parse("1.2.3-rc.1+abc123");

        Assert.Equal(version.ToString(), version.ToString(includeMetadata: true));
    }

    /// <summary>
    /// The library's own assembly has a readable informational version.
    /// </summary>
    [Fact]
    public void TryFromAssembly_reads_the_informational_version()
    {
        var ok = SemanticVersion.TryFromAssembly(typeof(SemanticVersion).Assembly, out var version);

        Assert.True(ok);
        Assert.NotNull(version);
    }

    /// <summary>
    /// Pre-release and build metadata come through, and the metadata does not matter when comparing.
    /// </summary>
    [Fact]
    public void TryFromAssembly_reads_prerelease_and_ignores_metadata_in_comparison()
    {
        var assembly = DynamicAssembly("1.4.0-rc.2+sha.abc");

        Assert.True(SemanticVersion.TryFromAssembly(assembly, out var version));
        Assert.Equal(SemanticVersion.Parse("1.4.0-rc.2"), version);
        Assert.Equal("sha.abc", version!.BuildMetadata);
    }

    /// <summary>
    /// An assembly without the attribute gives nothing instead of 0.0.0.
    /// </summary>
    [Fact]
    public void TryFromAssembly_without_the_attribute_gives_nothing_instead_of_zero()
    {
        var assembly = DynamicAssembly(null);

        Assert.False(SemanticVersion.TryFromAssembly(assembly, out var version));
        Assert.Null(version);
    }

    /// <summary>
    /// Throttling and the recorded time follow the supplied clock.
    /// </summary>
    [Fact]
    public async Task Throttling_uses_the_supplied_time_provider()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip") };
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryLastCheckStore();
        using var updater = new ReleaseUpdater(Options(store, clock), client);

        var first = await updater.CheckForUpdateAsync(cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(23));
        var tooSoon = await updater.CheckForUpdateAsync(cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));
        var later = await updater.CheckForUpdateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(first.Throttled);
        Assert.True(tooSoon.Throttled);
        Assert.False(later.Throttled);
        Assert.Equal(clock.GetUtcNow(), await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A store that cannot record the check does not discard its result, but the failure is on the result.
    /// </summary>
    [Fact]
    public async Task A_store_that_cannot_record_the_check_does_not_discard_its_result()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip") };
        using var updater = new ReleaseUpdater(Options(new WriteFailingStore(), TimeProvider.System), client);

        var result = await updater.CheckForUpdateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.IsUpdateAvailable);
        Assert.IsType<IOException>(result.LastCheckStoreError);
    }

    /// <summary>
    /// A store that records fine leaves no error on the result.
    /// </summary>
    [Fact]
    public async Task A_store_that_records_the_check_leaves_no_error()
    {
        var client = new FakeReleaseClient { Latest = TestData.Release("v2.0.0", "app-win-x64.zip") };
        using var updater = new ReleaseUpdater(Options(new InMemoryLastCheckStore(), TimeProvider.System), client);

        var result = await updater.CheckForUpdateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.LastCheckStoreError);
    }

    /// <summary>
    /// By default a platform-less installer is not chosen.
    /// </summary>
    [Fact]
    public void Platformless_installer_is_not_chosen_by_default()
    {
        var release = TestData.Release("v1.0.0", "MyApp-Setup-1.0.0.exe", "SHA256SUMS");

        Assert.Null(new RuntimeAssetSelector(Win64).Select(release));
    }

    /// <summary>
    /// With the opt-in a platform-less installer is chosen.
    /// </summary>
    [Fact]
    public void Platformless_installer_is_chosen_when_allowed()
    {
        var release = TestData.Release("v1.0.0", "MyApp-Setup-1.0.0.exe", "SHA256SUMS");

        var selected = new RuntimeAssetSelector(Win64) { AllowUnknownPlatform = true }.Select(release);

        Assert.Equal("MyApp-Setup-1.0.0.exe", selected?.Name);
    }

    /// <summary>
    /// With the opt-in the installer is chosen even when it is listed after notes and symbols.
    /// </summary>
    [Fact]
    public void Platformless_installer_wins_over_notes_and_symbols_listed_before_it()
    {
        var release = TestData.Release("v1.0.0", "symbols.zip", "RELEASE_NOTES.md", "MyApp-Setup-1.0.0.exe");

        var selected = new RuntimeAssetSelector(Win64) { AllowUnknownPlatform = true }.Select(release);

        Assert.Equal("MyApp-Setup-1.0.0.exe", selected?.Name);
    }

    /// <summary>
    /// With the opt-in files that are not something to install are still not chosen.
    /// </summary>
    [Fact]
    public void Platformless_files_that_are_not_installers_are_never_chosen()
    {
        var release = TestData.Release("v1.0.0", "symbols.zip", "RELEASE_NOTES.md", "source.tar.gz");

        Assert.Null(new RuntimeAssetSelector(Win64) { AllowUnknownPlatform = true }.Select(release));
    }

    /// <summary>
    /// An extension the caller prefers makes a platform-less file of that kind eligible.
    /// </summary>
    [Fact]
    public void Preferred_extension_makes_a_platformless_file_eligible()
    {
        var release = TestData.Release("v1.0.0", "RELEASE_NOTES.md", "MyApp-portable.zip");

        var selected = new RuntimeAssetSelector(Win64, ".zip") { AllowUnknownPlatform = true }.Select(release);

        Assert.Equal("MyApp-portable.zip", selected?.Name);
    }

    /// <summary>
    /// A matching platform wins over a platform-less asset.
    /// </summary>
    [Fact]
    public void Matching_platform_wins_over_platformless_asset_even_when_allowed()
    {
        var release = TestData.Release("v1.0.0", "MyApp-Setup-1.0.0.exe", "MyApp-win-x64.zip");

        var selected = new RuntimeAssetSelector(Win64) { AllowUnknownPlatform = true }.Select(release);

        Assert.Equal("MyApp-win-x64.zip", selected?.Name);
    }

    /// <summary>
    /// Assets for another platform are never chosen, whatever the opt-in.
    /// </summary>
    [Fact]
    public void Other_platform_is_never_chosen_even_when_unknown_platforms_are_allowed()
    {
        var release = TestData.Release("v1.0.0", "MyApp-linux-x64.tar.gz", "MyApp-win-arm64.zip");

        var selected = new RuntimeAssetSelector(Win64) { AllowUnknownPlatform = true }.Select(release);

        Assert.Null(selected);
    }

    /// <summary>
    /// Builds updater options at version 1.0.0 that throttle to once in 24 hours.
    /// </summary>
    /// <param name="store">The last-check store.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The options.</returns>
    private static UpdaterOptions Options(ILastCheckStore store, TimeProvider time) => new()
    {
        Owner = "o",
        Repo = "r",
        CurrentVersion = "1.0.0",
        AssetSelector = new RuntimeAssetSelector(Win64),
        LastCheckStore = store,
        MinimumCheckInterval = TimeSpan.FromHours(24),
        TimeProvider = time,
    };

    /// <summary>
    /// Builds an in-memory assembly, optionally with an informational version.
    /// </summary>
    /// <param name="informationalVersion">The version to put in the attribute, or null for none.</param>
    /// <returns>The assembly.</returns>
    private static Assembly DynamicAssembly(string? informationalVersion)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Dyn" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        if (informationalVersion is not null)
        {
            var constructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!;
            builder.SetCustomAttribute(new CustomAttributeBuilder(constructor, [informationalVersion]));
        }

        return builder;
    }

    /// <summary>
    /// A clock that only moves when the test says so.
    /// </summary>
    /// <param name="start">The time it starts at.</param>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        /// <summary>
        /// The current time.
        /// </summary>
        private DateTimeOffset _now = start;

        /// <summary>
        /// Moves the clock forward.
        /// </summary>
        /// <param name="by">How far to move.</param>
        public void Advance(TimeSpan by) => _now += by;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// A last-check store that reads nothing and fails to write the time of a check.
    /// </summary>
    private sealed class WriteFailingStore : ILastCheckStore
    {
        /// <inheritdoc />
        public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateTimeOffset?>(null);

        /// <inheritdoc />
        public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<SemanticVersion?>(null);

        /// <inheritdoc />
        public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default) => throw new IOException("disk full");

        /// <inheritdoc />
        public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
