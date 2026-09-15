namespace GitHubReleaseUpdater.Exceptions;

/// <summary>Raised when no release asset satisfies the configured <see cref="Assets.IAssetSelector"/>.</summary>
public sealed class AssetNotFoundException : UpdaterException
{
    /// <summary>Names of the assets that were available on the release.</summary>
    public IReadOnlyList<string> AvailableAssets { get; }

    /// <inheritdoc />
    public AssetNotFoundException() : this("No matching release asset was found.", []) { }
    /// <inheritdoc />
    public AssetNotFoundException(string message) : this(message, []) { }
    /// <inheritdoc />
    public AssetNotFoundException(string message, Exception? innerException) : base(message, innerException) { AvailableAssets = []; }

    /// <summary>Creates a new instance.</summary>
    public AssetNotFoundException(string message, IReadOnlyList<string> availableAssets) : base(message)
    {
        AvailableAssets = availableAssets;
    }
}
