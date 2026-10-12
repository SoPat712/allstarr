using allstarr.Core.Music;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class MusicTopSongsTests
{
    [Fact]
    public void AddsTheArtistsProviderSongsUpToTheCount()
    {
        var added = MusicTopSongs.Extend("Grimes", ["Oblivion"],
            [Song("1", "Genesis", "Grimes"), Song("2", "Realiti", "Grimes"), Song("3", "Kill V. Maim", "Grimes")], 2);

        Assert.Equal(["1", "2"], added.Select(song => song.Id));
    }

    [Fact]
    public void KeepsLeadCreditCollaborationsAndRejectsNamesakes()
    {
        var added = MusicTopSongs.Extend("Grimes",
            [],
            [
                Song("namesake", "Grimes Song", "Gary Grimes"),
                Song("featured", "Venus Fly", "Grimes feat. Janelle Monáe"),
                Song("guest", "Pretty Dark", "Bladee & Grimes"),
                Song("exact", "Flesh without Blood", "grimes")
            ],
            10);

        Assert.Equal(["featured", "exact"], added.Select(song => song.Id));
    }

    [Fact]
    public void SkipsTitlesAlreadyListedOrRepeated()
    {
        var added = MusicTopSongs.Extend("Grimes", ["Oblivion"],
            [Song("1", "oblivion", "Grimes"), Song("2", "Genesis", "Grimes"), Song("3", "Genesis", "Grimes")], 10);

        Assert.Equal(["2"], added.Select(song => song.Id));
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("Grimes", 0)]
    public void NothingIsAddedWithoutAnArtistOrRoom(string artist, int count) =>
        Assert.Empty(MusicTopSongs.Extend(artist, [], [Song("1", "Genesis", "Grimes")], count));

    private static Song Song(string id, string title, string artist) =>
        new() { Id = id, Title = title, Artist = artist, ExternalProvider = "deezer", ExternalId = id };
}
