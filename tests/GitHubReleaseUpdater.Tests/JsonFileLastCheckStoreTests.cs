using GitHubReleaseUpdater.LastCheck;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater.Tests;

/// <summary>
/// Tests for <see cref="JsonFileLastCheckStore"/>, using a real temporary directory per test.
/// </summary>
public sealed class JsonFileLastCheckStoreTests : IDisposable
{
    /// <summary>
    /// The directory the state file lives in, unique per test; it does not exist until something is written.
    /// </summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ghru-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Gets the path of the state file, inside a subdirectory that has to be created.
    /// </summary>
    private string StatePath => Path.Combine(_directory, "nested", "update-state.json");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// Without a file nothing is recorded.
    /// </summary>
    [Fact]
    public async Task Missing_file_means_nothing_recorded()
    {
        var store = new JsonFileLastCheckStore(StatePath);

        Assert.Null(await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
        Assert.Null(await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Values are read back by a new store over the same file, and the directory is created on the first write.
    /// </summary>
    [Fact]
    public async Task Values_survive_a_new_store_over_the_same_file_and_the_directory_is_created()
    {
        var checkedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var first = new JsonFileLastCheckStore(StatePath);
        await first.SetLastCheckedAtAsync(checkedAt, TestContext.Current.CancellationToken);
        await first.SetSkippedVersionAsync(SemanticVersion.Parse("2.0.0-rc.1"), TestContext.Current.CancellationToken);

        var second = new JsonFileLastCheckStore(StatePath);

        Assert.Equal(checkedAt, await second.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SemanticVersion.Parse("2.0.0-rc.1"), await second.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Setting one value keeps the other.
    /// </summary>
    [Fact]
    public async Task Setting_one_value_keeps_the_other()
    {
        var checkedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new JsonFileLastCheckStore(StatePath);
        await store.SetSkippedVersionAsync(SemanticVersion.Parse("3.0.0"), TestContext.Current.CancellationToken);

        await store.SetLastCheckedAtAsync(checkedAt, TestContext.Current.CancellationToken);

        Assert.Equal(SemanticVersion.Parse("3.0.0"), await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Clearing the skipped version leaves the time of the last check.
    /// </summary>
    [Fact]
    public async Task ClearSkippedVersion_removes_only_the_skipped_version()
    {
        var checkedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new JsonFileLastCheckStore(StatePath);
        await store.SetLastCheckedAtAsync(checkedAt, TestContext.Current.CancellationToken);
        await store.SetSkippedVersionAsync(SemanticVersion.Parse("3.0.0"), TestContext.Current.CancellationToken);

        await store.ClearSkippedVersionAsync(TestContext.Current.CancellationToken);

        Assert.Null(await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal(checkedAt, await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A corrupt file counts as empty and is replaced by the next write.
    /// </summary>
    [Fact]
    public async Task Corrupt_file_is_treated_as_empty_and_replaced_on_the_next_write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{ not json", TestContext.Current.CancellationToken);
        var store = new JsonFileLastCheckStore(StatePath);
        var checkedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
        await store.SetLastCheckedAtAsync(checkedAt, TestContext.Current.CancellationToken);

        Assert.Equal(checkedAt, await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A skipped version that is not a version number is ignored.
    /// </summary>
    [Fact]
    public async Task Unparsable_skipped_version_is_ignored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{\"skippedVersion\":\"nightly\"}", TestContext.Current.CancellationToken);

        Assert.Null(await new JsonFileLastCheckStore(StatePath).GetSkippedVersionAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A file another program holds open reads as empty, so a check is not blocked by it.
    /// </summary>
    [Fact]
    public async Task Locked_file_reads_as_empty()
    {
        var store = new JsonFileLastCheckStore(StatePath);
        await store.SetLastCheckedAtAsync(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);
        using var holder = new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    /// <summary>
    /// Writing to a locked file fails instead of replacing what it holds with a state that lost the other value.
    /// </summary>
    [Fact]
    public async Task Write_to_a_locked_file_throws_and_keeps_the_content()
    {
        var store = new JsonFileLastCheckStore(StatePath);
        await store.SetSkippedVersionAsync(SemanticVersion.Parse("3.0.0"), TestContext.Current.CancellationToken);
        using (new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.SetLastCheckedAtAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        }

        Assert.Equal(SemanticVersion.Parse("3.0.0"), await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A lock that another program drops within a moment, as antivirus does, only delays the write.
    /// </summary>
    [Fact]
    public async Task Write_to_a_briefly_locked_file_is_retried_and_succeeds()
    {
        // Only Windows reports a held file as a sharing violation, which is what the retry looks for.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "File sharing violations are reported by Windows only.");
        var store = new JsonFileLastCheckStore(StatePath);
        await store.SetSkippedVersionAsync(SemanticVersion.Parse("3.0.0"), TestContext.Current.CancellationToken);
        var checkedAt = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var holder = new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            holder.Dispose();
        }, TestContext.Current.CancellationToken);

        await store.SetLastCheckedAtAsync(checkedAt, TestContext.Current.CancellationToken);
        await release;

        Assert.Equal(checkedAt, await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SemanticVersion.Parse("3.0.0"), await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Writes from several tasks leave a valid file and no temporary files.
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_leave_a_valid_file_and_no_temporary_files()
    {
        var store = new JsonFileLastCheckStore(StatePath);
        var times = Enumerable.Range(0, 20).Select(i => new DateTimeOffset(2026, 1, 1, 0, 0, i, TimeSpan.Zero)).ToArray();

        await Task.WhenAll(times.Select(t => store.SetLastCheckedAtAsync(t, TestContext.Current.CancellationToken)));

        Assert.Contains(await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken), times.Cast<DateTimeOffset?>());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StatePath)!));
    }

    /// <summary>
    /// A blank path is refused.
    /// </summary>
    [Fact]
    public void Rejects_a_blank_path()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileLastCheckStore(" "));
    }

    /// <summary>
    /// A pending update survives a restart and makes the store report no check, so the next one is not throttled.
    /// </summary>
    [Fact]
    public async Task Pending_update_is_persisted_and_hides_the_check_time()
    {
        var store = new JsonFileLastCheckStore(StatePath);
        await store.SetLastCheckedAtAsync(DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);
        await store.SetUpdatePendingAsync(true, TestContext.Current.CancellationToken);

        var reopened = new JsonFileLastCheckStore(StatePath);
        Assert.Null(await reopened.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));

        await reopened.SetUpdatePendingAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(DateTimeOffset.UnixEpoch, await reopened.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Setting the pending flag to the value it already has does not rewrite the file.
    /// </summary>
    [Fact]
    public async Task Unchanged_pending_flag_does_not_rewrite_the_file()
    {
        var store = new JsonFileLastCheckStore(StatePath);
        await store.SetUpdatePendingAsync(true, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(StatePath, DateTime.UnixEpoch);

        await store.SetUpdatePendingAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(StatePath));
    }
}
