using allstarr.Services.Common;

namespace allstarr.Tests;

public class InjectedPlaylistItemHelperTests
{
    [Fact]
    public void LooksLikeSyntheticLocalItem_ReturnsTrue_ForLocalAllstarrItem()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "49cf417c0fe00ad9cb1ed59f2debc384",
            ["ServerId"] = "allstarr"
        };

        Assert.True(InjectedPlaylistItemHelper.LooksLikeSyntheticLocalItem(item));
    }

    [Fact]
    public void LooksLikeSyntheticLocalItem_ReturnsFalse_ForExternalInjectedItem()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "ext-spotify-4h4QlmocP3IuwYEj2j14p8",
            ["ServerId"] = "allstarr"
        };

        Assert.False(InjectedPlaylistItemHelper.LooksLikeSyntheticLocalItem(item));
    }

    [Fact]
    public void LooksLikeSyntheticLocalItem_ReturnsFalse_ForRawJellyfinItem()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "49cf417c0fe00ad9cb1ed59f2debc384",
            ["ServerId"] = "c17d351d3af24c678a6d8049c212d522"
        };

        Assert.False(InjectedPlaylistItemHelper.LooksLikeSyntheticLocalItem(item));
    }

    [Fact]
    public void LooksLikeLocalItemMissingGenreMetadata_ReturnsTrue_ForRawJellyfinItemMissingGenreItems()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "49cf417c0fe00ad9cb1ed59f2debc384",
            ["ServerId"] = "c17d351d3af24c678a6d8049c212d522",
            ["Genres"] = new[] { "Pop" }
        };

        Assert.True(InjectedPlaylistItemHelper.LooksLikeLocalItemMissingGenreMetadata(item));
    }

    [Fact]
    public void LooksLikeLocalItemMissingGenreMetadata_ReturnsFalse_WhenGenresAndGenreItemsExist()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "49cf417c0fe00ad9cb1ed59f2debc384",
            ["ServerId"] = "c17d351d3af24c678a6d8049c212d522",
            ["Genres"] = new[] { "Pop" },
            ["GenreItems"] = new[]
            {
                new Dictionary<string, object?> { ["Name"] = "Pop", ["Id"] = "genre-id" }
            }
        };

        Assert.False(InjectedPlaylistItemHelper.LooksLikeLocalItemMissingGenreMetadata(item));
    }

    [Fact]
    public void LooksLikeLocalItemMissingGenreMetadata_ReturnsFalse_ForExternalInjectedItem()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "ext-spotify-4h4QlmocP3IuwYEj2j14p8",
            ["ServerId"] = "allstarr",
            ["Genres"] = new[] { "Pop" }
        };

        Assert.False(InjectedPlaylistItemHelper.LooksLikeLocalItemMissingGenreMetadata(item));
    }

    [Theory]
    [InlineData("ext-deezer-song-123", "Track [S]")]
    [InlineData("ext-deezer-song-123", "Track [S] [E]")]
    [InlineData("ext-qobuz-song-123", "Track [S]")]
    public void LooksLikeLegacyExternalSourceLabeledItem_ReturnsTrue_ForRelabeledProviders(
        string id,
        string name)
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["Name"] = name
        };

        Assert.True(InjectedPlaylistItemHelper.LooksLikeLegacyExternalSourceLabeledItem(item));
    }

    [Theory]
    [InlineData("ext-deezer-song-123", "Track [D]")]
    [InlineData("ext-qobuz-song-123", "Track [Q]")]
    [InlineData("ext-squidwtf-song-123", "Track [S]")]
    [InlineData("local-song-123", "Track [S]")]
    public void LooksLikeLegacyExternalSourceLabeledItem_ReturnsFalse_ForCurrentLabels(
        string id,
        string name)
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["Name"] = name
        };

        Assert.False(InjectedPlaylistItemHelper.LooksLikeLegacyExternalSourceLabeledItem(item));
    }

    [Fact]
    public void LooksLikeUnavailableExternalItem_ReturnsTrue_ForTypedSquidId()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "ext-squidwtf-song-468724403",
            ["ServerId"] = "allstarr"
        };

        Assert.True(InjectedPlaylistItemHelper.LooksLikeUnavailableExternalItem(item));
    }

    [Fact]
    public void LooksLikeUnavailableExternalItem_ReturnsTrue_ForLegacyHashedIdWithProviderIds()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "e052a314b638ce85f68de4f11c076c9a",
            ["ServerId"] = "allstarr",
            ["ProviderIds"] = new Dictionary<string, string>
            {
                ["squidwtf"] = "468724403",
                ["Spotify"] = "3oTuTpF1F3A7rEC6RKsMRz"
            }
        };

        Assert.True(InjectedPlaylistItemHelper.LooksLikeUnavailableExternalItem(item));
    }

    [Fact]
    public void LooksLikeUnavailableExternalItem_ReturnsFalse_ForRawJellyfinItem()
    {
        var item = new Dictionary<string, object?>
        {
            ["Id"] = "e052a314b638ce85f68de4f11c076c9a",
            ["ServerId"] = "real-jellyfin-server",
            ["ProviderIds"] = new Dictionary<string, string> { ["squidwtf"] = "468724403" }
        };

        Assert.False(InjectedPlaylistItemHelper.LooksLikeUnavailableExternalItem(item));
    }

    [Fact]
    public void RemoveUnavailableExternalItems_PreservesOrderAndPlayableEntries()
    {
        var local = new Dictionary<string, object?>
        {
            ["Id"] = "local-1",
            ["ServerId"] = "real-jellyfin-server"
        };
        var blocked = new Dictionary<string, object?>
        {
            ["Id"] = "ext-squidwtf-song-25",
            ["ServerId"] = "allstarr"
        };
        var deezer = new Dictionary<string, object?>
        {
            ["Id"] = "ext-deezer-song-42",
            ["ServerId"] = "allstarr"
        };

        var result = InjectedPlaylistItemHelper.RemoveUnavailableExternalItems(
            [local, blocked, deezer]);

        Assert.Equal([local, deezer], result);
    }
}
