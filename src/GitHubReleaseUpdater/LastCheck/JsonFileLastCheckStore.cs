using GitHubReleaseUpdater.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitHubReleaseUpdater.LastCheck;

/// <summary>
/// An <see cref="ILastCheckStore"/> that keeps its state in a small JSON file, so throttling and a skipped version
/// survive a restart of the application. Writes go to a temporary file that then replaces the real one, so a crash
/// never leaves a half-written file; a missing, unreadable or corrupt file is treated as "nothing recorded". Safe to
/// share between threads of one process. The format is private to this library.
/// </summary>
public sealed partial class JsonFileLastCheckStore : ILastCheckStore, IPendingUpdateStore
{
    /// <summary>
    /// Full path of the state file.
    /// </summary>
    private readonly string _path;

    /// <summary>
    /// Serializes reads and writes of the file within this process.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Creates a store over a file. The file and its directory are created on the first write.
    /// </summary>
    /// <param name="path">Path of the state file.</param>
    public JsonFileLastCheckStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return state.UpdatePending ? null : state.LastCheckedAt;
    }

    /// <inheritdoc />
    public async Task SetUpdatePendingAsync(bool pending, CancellationToken cancellationToken = default)
    {
        // Checks repeat the same answer for as long as a release stays pending; the file is only written when it changes.
        if ((await ReadAsync(cancellationToken).ConfigureAwait(false)).UpdatePending == pending)
        {
            return;
        }

        await UpdateAsync(state => state.UpdatePending = pending, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default)
    {
        var text = (await ReadAsync(cancellationToken).ConfigureAwait(false)).SkippedVersion;
        return SemanticVersion.TryParse(text, out var version) ? version : null;
    }

    /// <inheritdoc />
    public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default)
    {
        return UpdateAsync(state => state.LastCheckedAt = checkedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        return UpdateAsync(state => state.SkippedVersion = version.ToString(), cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default)
    {
        return UpdateAsync(state => state.SkippedVersion = null, cancellationToken);
    }

    /// <summary>
    /// Reads the state, or an empty one if the file is missing or cannot be parsed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored state.</returns>
    private async Task<StoreState> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(tolerateUnreadable: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the state without taking the lock.
    /// </summary>
    /// <param name="tolerateUnreadable">
    /// True to treat a file that cannot be opened (locked by another program, access denied) as empty, which is right
    /// for answering a question. False to let that error out, which is right before a write: replacing a file that could
    /// not be read would throw away what it holds.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored state, empty if there is none.</returns>
    private async Task<StoreState> ReadUnlockedAsync(bool tolerateUnreadable, CancellationToken cancellationToken)
    {
        try
        {
            return await FileRetry.RunAsync(async () =>
            {
                await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                return await JsonSerializer.DeserializeAsync(stream, StoreJsonContext.Default.StoreState, cancellationToken).ConfigureAwait(false) ?? new StoreState();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Nothing recorded yet, or a damaged file: either way the next check simply is not postponed.
            return new StoreState();
        }
        catch (Exception ex) when (tolerateUnreadable && ex is IOException or UnauthorizedAccessException)
        {
            // Locked or not allowed: for a question that is the same as "nothing recorded"; a failed write will say so.
            return new StoreState();
        }
    }

    /// <summary>
    /// Reads the state, applies a change and writes it back through a temporary file.
    /// </summary>
    /// <param name="change">Changes the state.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>A task that completes when the file is written.</returns>
    private async Task UpdateAsync(Action<StoreState> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadUnlockedAsync(tolerateUnreadable: false, cancellationToken).ConfigureAwait(false);
            change(state);

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(state, StoreJsonContext.Default.StoreState);
            await FileRetry.RunAsync(async () =>
            {
                var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
                    File.Move(temp, _path, overwrite: true);
                    return true;
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The content of the state file.
    /// </summary>
    internal sealed class StoreState
    {
        /// <summary>
        /// When the last check completed.
        /// </summary>
        [JsonPropertyName("lastCheckedAt")]
        public DateTimeOffset? LastCheckedAt { get; set; }

        /// <summary>
        /// The version the user chose to skip, as text.
        /// </summary>
        [JsonPropertyName("skippedVersion")]
        public string? SkippedVersion { get; set; }

        /// <summary>
        /// Whether the last successful check found a newer release.
        /// </summary>
        [JsonPropertyName("updatePending")]
        public bool UpdatePending { get; set; }
    }

    /// <summary>
    /// Source-generated JSON context for the state file (trimming/AOT friendly).
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StoreState))]
    internal sealed partial class StoreJsonContext : JsonSerializerContext
    {
    }
}
