using System.Net;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Versioning;

namespace GitHubReleaseUpdater.Tests;

public class ExceptionsTests
{
    [Fact]
    public void UpdaterException_default_constructor_has_no_message_override()
        => Assert.NotNull(new UpdaterException().Message);

    [Fact]
    public void UpdaterException_message_constructor_sets_message()
        => Assert.Equal("boom", new UpdaterException("boom").Message);

    [Fact]
    public void UpdaterException_message_and_inner_constructor_sets_both()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new UpdaterException("boom", inner);
        Assert.Equal("boom", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void AssetNotFoundException_default_constructor_has_message_and_empty_assets()
    {
        var ex = new AssetNotFoundException();
        Assert.Equal("No matching release asset was found.", ex.Message);
        Assert.Empty(ex.AvailableAssets);
    }

    [Fact]
    public void AssetNotFoundException_message_constructor_has_empty_assets()
    {
        var ex = new AssetNotFoundException("custom");
        Assert.Equal("custom", ex.Message);
        Assert.Empty(ex.AvailableAssets);
    }

    [Fact]
    public void AssetNotFoundException_message_and_inner_constructor()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new AssetNotFoundException("custom", inner);
        Assert.Equal("custom", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Empty(ex.AvailableAssets);
    }

    [Fact]
    public void AssetNotFoundException_carries_available_assets()
    {
        var ex = new AssetNotFoundException("custom", new[] { "a.zip", "b.zip" });
        Assert.Equal(["a.zip", "b.zip"], ex.AvailableAssets);
    }

    [Fact]
    public void ChecksumMismatchException_default_constructor_has_empty_fields()
    {
        var ex = new ChecksumMismatchException();
        Assert.Equal(string.Empty, ex.FilePath);
        Assert.Equal(string.Empty, ex.Expected);
        Assert.Equal(string.Empty, ex.Actual);
    }

    [Fact]
    public void ChecksumMismatchException_message_constructor_leaves_fields_empty()
    {
        var ex = new ChecksumMismatchException("custom");
        Assert.Equal("custom", ex.Message);
        Assert.Equal(string.Empty, ex.FilePath);
    }

    [Fact]
    public void ChecksumMismatchException_message_and_inner_constructor()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new ChecksumMismatchException("custom", inner);
        Assert.Equal("custom", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void ChecksumMismatchException_full_constructor_formats_message_and_sets_fields()
    {
        var ex = new ChecksumMismatchException("/tmp/app.zip", "aaa", "bbb");
        Assert.Equal("/tmp/app.zip", ex.FilePath);
        Assert.Equal("aaa", ex.Expected);
        Assert.Equal("bbb", ex.Actual);
        Assert.Equal("Checksum mismatch for '/tmp/app.zip': expected aaa, got bbb.", ex.Message);
    }

    [Fact]
    public void GitHubApiException_default_constructor_has_defaults()
    {
        var ex = new GitHubApiException();
        Assert.Equal("GitHub API request failed.", ex.Message);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.False(ex.IsRateLimited);
        Assert.Null(ex.RateLimitResetAt);
        Assert.Equal(string.Empty, ex.ResponseBody);
    }

    [Fact]
    public void GitHubApiException_message_constructor_uses_defaults()
    {
        var ex = new GitHubApiException("custom");
        Assert.Equal("custom", ex.Message);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public void GitHubApiException_message_and_inner_constructor_sets_defaults_and_inner()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new GitHubApiException("custom", inner);
        Assert.Equal("custom", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal(string.Empty, ex.ResponseBody);
    }

    [Fact]
    public void GitHubApiException_full_constructor_sets_all_properties()
    {
        var reset = DateTimeOffset.UtcNow;
        var ex = new GitHubApiException("custom", HttpStatusCode.Forbidden, true, reset, "{}");
        Assert.Equal("custom", ex.Message);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.True(ex.IsRateLimited);
        Assert.Equal(reset, ex.RateLimitResetAt);
        Assert.Equal("{}", ex.ResponseBody);
    }

    [Fact]
    public void All_exceptions_derive_from_UpdaterException()
    {
        Assert.IsAssignableFrom<UpdaterException>(new AssetNotFoundException());
        Assert.IsAssignableFrom<UpdaterException>(new ChecksumMismatchException());
        Assert.IsAssignableFrom<UpdaterException>(new GitHubApiException());
    }

    [Fact]
    public void VersionParseException_default_constructor_has_generic_message()
    {
        var ex = new VersionParseException();
        Assert.Equal("Invalid semantic version.", ex.Message);
        Assert.Equal(string.Empty, ex.Input);
    }

    [Fact]
    public void VersionParseException_message_and_inner_constructor_leaves_input_empty()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new VersionParseException("custom", inner);
        Assert.Equal("custom", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(string.Empty, ex.Input);
    }

    [Fact]
    public void VersionParseException_input_constructor_formats_message_and_sets_input()
    {
        var ex = new VersionParseException("bad-version");
        Assert.Equal("bad-version", ex.Input);
        Assert.Equal("'bad-version' is not a valid semantic version.", ex.Message);
    }
}
