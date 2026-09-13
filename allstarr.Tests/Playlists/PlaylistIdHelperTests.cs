using allstarr.Services.Common;
using Xunit;

namespace allstarr.Tests;

public class PlaylistIdHelperTests
{

    [Fact]
    public void IsExternalPlaylist_WithValidPlaylistId_ReturnsTrue()
    {
        var id = "ext-deezer-playlist-123456";

        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        Assert.True(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithValidQobuzPlaylistId_ReturnsTrue()
    {
        var id = "ext-qobuz-playlist-789012";

        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        Assert.True(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithUpperCasePrefix_ReturnsTrue()
    {
        var id = "EXT-deezer-PLAYLIST-123456";

        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        Assert.True(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithRegularAlbumId_ReturnsFalse()
    {
        var id = "ext-deezer-album-123456";

        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        Assert.False(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithNullId_ReturnsFalse()
    {
        string? id = null;

        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        Assert.False(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithEmptyString_ReturnsFalse()
    {
        var id = "";

        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        Assert.False(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithRandomString_ReturnsFalse()
    {
        var id = "random-string-123";

        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        Assert.False(result);
    }



    [Fact]
    public void ParsePlaylistId_WithValidDeezerPlaylistId_ReturnsProviderAndExternalId()
    {
        var id = "ext-deezer-playlist-123456";

        var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);

        Assert.Equal("deezer", provider);
        Assert.Equal("123456", externalId);
    }

    [Fact]
    public void ParsePlaylistId_WithValidQobuzPlaylistId_ReturnsProviderAndExternalId()
    {
        var id = "ext-qobuz-playlist-789012";

        var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);

        Assert.Equal("qobuz", provider);
        Assert.Equal("789012", externalId);
    }

    [Fact]
    public void ParsePlaylistId_WithExternalIdContainingDashes_ParsesCorrectly()
    {
        var id = "ext-deezer-playlist-abc-def-123";

        var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);

        Assert.Equal("deezer", provider);
        Assert.Equal("abc-def-123", externalId);
    }

    [Fact]
    public void ParsePlaylistId_WithInvalidFormatNoProvider_ThrowsArgumentException()
    {
        var id = "ext-playlist-123456";

        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id));
        Assert.Contains("Invalid playlist ID format", exception.Message);
    }

    [Fact]
    public void ParsePlaylistId_WithNonPlaylistId_ThrowsArgumentException()
    {
        var id = "ext-deezer-album-123456";

        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id));
        Assert.Contains("Invalid playlist ID format", exception.Message);
    }

    [Fact]
    public void ParsePlaylistId_WithNullId_ThrowsArgumentException()
    {
        string? id = null;

        Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id!));
    }

    [Fact]
    public void ParsePlaylistId_WithEmptyString_ThrowsArgumentException()
    {
        var id = "";

        Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id));
    }

    [Fact]
    public void ParsePlaylistId_WithOnlyPrefix_ThrowsArgumentException()
    {
        var id = "ext-";

        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id));
        Assert.Contains("Invalid playlist ID format", exception.Message);
    }



    [Fact]
    public void CreatePlaylistId_WithValidDeezerProviderAndId_ReturnsCorrectFormat()
    {
        var provider = "deezer";
        var externalId = "123456";

        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        Assert.Equal("ext-deezer-playlist-123456", result);
    }

    [Fact]
    public void CreatePlaylistId_WithValidQobuzProviderAndId_ReturnsCorrectFormat()
    {
        var provider = "qobuz";
        var externalId = "789012";

        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        Assert.Equal("ext-qobuz-playlist-789012", result);
    }

    [Fact]
    public void CreatePlaylistId_WithUpperCaseProvider_ConvertsToLowerCase()
    {
        var provider = "DEEZER";
        var externalId = "123456";

        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        Assert.Equal("ext-deezer-playlist-123456", result);
    }

    [Fact]
    public void CreatePlaylistId_WithMixedCaseProvider_ConvertsToLowerCase()
    {
        var provider = "DeEzEr";
        var externalId = "123456";

        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        Assert.Equal("ext-deezer-playlist-123456", result);
    }

    [Fact]
    public void CreatePlaylistId_WithExternalIdContainingDashes_PreservesDashes()
    {
        var provider = "deezer";
        var externalId = "abc-def-123";

        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        Assert.Equal("ext-deezer-playlist-abc-def-123", result);
    }

    [Fact]
    public void CreatePlaylistId_WithNullProvider_ThrowsArgumentException()
    {
        string? provider = null;
        var externalId = "123456";

        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.CreatePlaylistId(provider!, externalId));
        Assert.Contains("Provider cannot be null or empty", exception.Message);
    }

    [Fact]
    public void CreatePlaylistId_WithEmptyProvider_ThrowsArgumentException()
    {
        var provider = "";
        var externalId = "123456";

        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.CreatePlaylistId(provider, externalId));
        Assert.Contains("Provider cannot be null or empty", exception.Message);
    }

    [Fact]
    public void CreatePlaylistId_WithNullExternalId_ThrowsArgumentException()
    {
        var provider = "deezer";
        string? externalId = null;

        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.CreatePlaylistId(provider, externalId!));
        Assert.Contains("External ID cannot be null or empty", exception.Message);
    }

    [Fact]
    public void CreatePlaylistId_WithEmptyExternalId_ThrowsArgumentException()
    {
        var provider = "deezer";
        var externalId = "";

        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.CreatePlaylistId(provider, externalId));
        Assert.Contains("External ID cannot be null or empty", exception.Message);
    }



    [Fact]
    public void RoundTrip_CreateAndParse_ReturnsOriginalValues()
    {
        var originalProvider = "deezer";
        var originalExternalId = "123456";

        var playlistId = PlaylistIdHelper.CreatePlaylistId(originalProvider, originalExternalId);
        var (parsedProvider, parsedExternalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

        Assert.Equal(originalProvider, parsedProvider);
        Assert.Equal(originalExternalId, parsedExternalId);
    }

    [Fact]
    public void RoundTrip_CreateWithUpperCaseAndParse_ReturnsLowerCaseProvider()
    {
        var originalProvider = "QOBUZ";
        var originalExternalId = "789012";

        var playlistId = PlaylistIdHelper.CreatePlaylistId(originalProvider, originalExternalId);
        var (parsedProvider, parsedExternalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

        Assert.Equal("qobuz", parsedProvider); // Converted to lowercase
        Assert.Equal(originalExternalId, parsedExternalId);
    }

    [Fact]
    public void RoundTrip_WithComplexExternalId_PreservesValue()
    {
        var originalProvider = "deezer";
        var originalExternalId = "abc-123-def-456";

        var playlistId = PlaylistIdHelper.CreatePlaylistId(originalProvider, originalExternalId);
        var (parsedProvider, parsedExternalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

        Assert.Equal(originalProvider, parsedProvider);
        Assert.Equal(originalExternalId, parsedExternalId);
    }

}
