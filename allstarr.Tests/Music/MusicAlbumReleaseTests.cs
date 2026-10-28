using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Controllers;
using allstarr.Services.Subsonic;
using System.Text.Json;
using System.Xml.Linq;

namespace allstarr.Tests;

public sealed class MusicAlbumReleaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchCollapsesProvenCopiesOfOneAlbumEdition(bool isExplicit)
    {
        var apple = Album("apple", "apple-download", "012345678905", isExplicit);
        var deezer = Album("deezer", "deezer", "0012345678905", isExplicit);
        deezer.ReleaseDate = "2026-09-03";

        var result = Merge(apple, deezer);

        Assert.Equal("apple", Assert.Single(result).Id);
        Assert.Equal("PRIMA", apple.Title);
        Assert.Equal("PRIMA", deezer.Title);
    }

    [Fact]
    public void SearchLabelsExplicitAndCleanOnlyWhenBothAppearWithoutMutatingSourceMetadata()
    {
        var explicitAlbum = Album("explicit", "apple-download", "012345678905", true);
        var clean = Album("clean", "apple-download", "012345678912", false);

        var result = Merge(explicitAlbum, clean);

        Assert.Equal(["PRIMA (Explicit)", "PRIMA (Clean)"], result.Select(album => album.Title));
        Assert.Equal("PRIMA", explicitAlbum.Title);
        Assert.Equal("PRIMA", clean.Title);
        Assert.Equal("PRIMA", Assert.Single(Merge(explicitAlbum)).Title);
        Assert.Equal("PRIMA", Assert.Single(Merge(clean)).Title);
    }

    [Theory]
    [InlineData("Deluxe")]
    [InlineData("Live")]
    [InlineData("Remix")]
    [InlineData("Stripped")]
    [InlineData("Extended Cut")]
    public void SearchPreservesDifferentNamedAlbumEditionsEvenWhenUpcConflicts(string edition)
    {
        var ordinary = Album("ordinary", "apple-download", "012345678905", true);
        var other = Album("other", "deezer", ordinary.Upc, true);
        other.Title += " (" + edition + ")";

        Assert.Equal(2, Merge(ordinary, other).Count);
    }

    [Fact]
    public void SearchNeverMergesOnTitleArtistDateAndTrackCountAlone()
    {
        var first = Album("first", "apple-download", null, true);
        var second = Album("second", "deezer", null, true);

        Assert.Equal(2, Merge(first, second).Count);
    }

    [Fact]
    public void SearchDoesNotLetUnknownRatingBridgeConflictingEditions()
    {
        var unknown = Album("unknown", "tidal", "012345678905", null);
        var explicitAlbum = Album("explicit", "apple-download", unknown.Upc, true);
        var clean = Album("clean", "deezer", unknown.Upc, false);

        var result = Merge(unknown, explicitAlbum, clean);

        Assert.Equal(3, result.Count);
        Assert.Equal("PRIMA", result.Single(album => album.Id == "unknown").Title);
        Assert.Equal("PRIMA (Explicit)", result.Single(album => album.Id == "explicit").Title);
        Assert.Equal("PRIMA (Clean)", result.Single(album => album.Id == "clean").Title);
    }

    [Fact]
    public void ContentEditionLabelsKeepExactTitleAndCombinedSearchRanking()
    {
        var explicitAlbum = Album("explicit", "apple-download", "012345678905", true);
        var clean = Album("clean", "apple-download", "012345678912", false);
        var other = Album("other", "deezer", null, null);
        other.Artist = "Another artist";
        var result = MusicSearchService.Merge<object>("PRIMA", [], [], [explicitAlbum, clean, other], [], [], false);

        Assert.Equal(["explicit", "clean", "other"], result.Albums.Select(item => item.Album!.Id));
        Assert.Equal(["explicit", "clean", "other"], MusicSearchService.RankTogether("PRIMA", result).Select(item => item.Album!.Id));
    }

    [Fact]
    public void JellyfinNativeAlbumWinsWithoutChangingItsCompletePayload()
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>("""
            {"Id":"native","Name":"PRIMA","Type":"MusicAlbum","AlbumArtist":"ADÉLA",
             "AlbumArtists":["ADÉLA"],"ChildCount":11,"OfficialRating":"Explicit",
             "ProviderIds":{"UPC":"012345678905"},"UnfamiliarField":{"keep":true}}
            """)!;
        var native = JellyfinController.ToNativeSearchItem(payload)!;
        var result = MusicSearchService.Merge<Dictionary<string, object?>>("PRIMA", [native], [],
            [Album("provider", "deezer", "012345678905", true)], [], [], false);

        Assert.Same(payload, Assert.Single(result.Albums).Native!.Payload);
        Assert.True(((JsonElement)payload["UnfamiliarField"]!).GetProperty("keep").GetBoolean());
        Assert.Equal("PRIMA", ((JsonElement)payload["Name"]!).GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubsonicNativeAlbumWinsInJsonAndXmlWithoutChangingItsCompletePayload(bool xml)
    {
        object payload = xml
            ? XElement.Parse("""<album id="native" name="PRIMA" artist="ADÉLA" songCount="11" upc="012345678905" explicitStatus="explicit" custom="keep"/>""")
            : new Dictionary<string, object>
            {
                ["id"] = "native",
                ["name"] = "PRIMA",
                ["artist"] = "ADÉLA",
                ["songCount"] = 11,
                ["upc"] = "012345678905",
                ["explicitStatus"] = "explicit",
                ["custom"] = "keep"
            };
        var native = SubsonicModelMapper.ToNativeItem(payload, MusicItemKind.Album);
        var result = MusicSearchService.Merge<object>("PRIMA", [native], [],
            [Album("provider", "deezer", "012345678905", true)], [], [], false);

        Assert.Same(payload, Assert.Single(result.Albums).Native!.Payload);
        Assert.Contains("keep", xml ? ((XElement)payload).ToString() : JsonSerializer.Serialize(payload));
    }

    private static IReadOnlyList<Album> Merge(params Album[] albums) => MusicSearchService.Merge<object>(
        "PRIMA", [], [], albums, [], [], false).Albums.Select(item => item.Album!).ToArray();

    private static Album Album(string id, string provider, string? upc, bool? isExplicit) => new()
    {
        Id = id,
        ExternalProvider = provider,
        ExternalId = id,
        Title = "PRIMA",
        Artist = "ADÉLA",
        Upc = upc,
        IsExplicit = isExplicit,
        ReleaseDate = "2026-09-04",
        Year = 2026,
        SongCount = 11
    };
}
