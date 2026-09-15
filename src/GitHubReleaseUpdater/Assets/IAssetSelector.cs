using GitHubReleaseUpdater.GitHub.Models;

namespace GitHubReleaseUpdater.Assets;

/// <summary>Chooses which asset of a release should be downloaded.</summary>
public interface IAssetSelector
{
    /// <summary>Returns the matching asset, or null when none matches.</summary>
    GitHubAsset? Select(GitHubRelease release);
}

/// <summary>Selector backed by a delegate.</summary>
public sealed class DelegateAssetSelector : IAssetSelector
{
    private readonly Func<GitHubRelease, GitHubAsset?> _selector;

    /// <summary>Creates a selector from a delegate that receives the whole release.</summary>
    public DelegateAssetSelector(Func<GitHubRelease, GitHubAsset?> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    /// <summary>Creates a selector from a predicate over asset names; the first match wins.</summary>
    public DelegateAssetSelector(Func<GitHubAsset, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _selector = r => r.Assets.FirstOrDefault(predicate);
    }

    /// <inheritdoc />
    public GitHubAsset? Select(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return _selector(release);
    }
}
