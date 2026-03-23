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
}
