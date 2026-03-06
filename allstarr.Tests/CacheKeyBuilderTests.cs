using allstarr.Services.Common;
using Xunit;

namespace allstarr.Tests;

public class CacheKeyBuilderTests
{
    [Fact]
    public void SearchKey_ShouldIncludeRouteContextDimensions()
    {
        var key = CacheKeyBuilder.BuildSearchKey(
            " DATA ",
            "MusicAlbum",
            500,
            0,
            "efa26829c37196b030fa31d127e0715b",
            "DateCreated,SortName",
            "Descending",
            true,
            "1635cd7d23144ba08251ebe22a56119e");

        Assert.Equal(
            "search:data:musicalbum:500:0:efa26829c37196b030fa31d127e0715b:datecreated,sortname:descending:true:1635cd7d23144ba08251ebe22a56119e",
            key);
    }

    [Fact]
    public void SearchKey_OldOverload_ShouldRemainCompatible()
    {
        Assert.Equal("search:data:Audio:500:0", CacheKeyBuilder.BuildSearchKey("DATA", "Audio", 500, 0));
    }

    [Fact]
    public void SpotifyKeys_ShouldMatchExpectedFormats()
    {
        Assert.Equal("spotify:playlist:Road Trip", CacheKeyBuilder.BuildSpotifyPlaylistKey("Road Trip"));
        Assert.Equal("spotify:playlist:items:Road Trip", CacheKeyBuilder.BuildSpotifyPlaylistItemsKey("Road Trip"));
        Assert.Equal("spotify:playlist:ordered:Road Trip", CacheKeyBuilder.BuildSpotifyPlaylistOrderedKey("Road Trip"));
        Assert.Equal("spotify:matched:ordered:Road Trip", CacheKeyBuilder.BuildSpotifyMatchedTracksKey("Road Trip"));
        Assert.Equal("spotify:matched:Road Trip", CacheKeyBuilder.BuildSpotifyLegacyMatchedTracksKey("Road Trip"));
        Assert.Equal("spotify:playlist:stats:Road Trip", CacheKeyBuilder.BuildSpotifyPlaylistStatsKey("Road Trip"));
        Assert.Equal("spotify:manual-map:Road Trip:abc123", CacheKeyBuilder.BuildSpotifyManualMappingKey("Road Trip", "abc123"));
        Assert.Equal("spotify:external-map:Road Trip:abc123", CacheKeyBuilder.BuildSpotifyExternalMappingKey("Road Trip", "abc123"));
        Assert.Equal("spotify:global-map:abc123", CacheKeyBuilder.BuildSpotifyGlobalMappingKey("abc123"));
        Assert.Equal("spotify:global-map:all-ids", CacheKeyBuilder.BuildSpotifyGlobalMappingsIndexKey());
    }

    [Fact]
    public void LyricsAndGenreKeys_ShouldMatchExpectedFormats()
    {
        Assert.Equal("lyrics:Artist:Title:Album:240", CacheKeyBuilder.BuildLyricsKey("Artist", "Title", "Album", 240));
        Assert.Equal("lyricsplus:Artist:Title:Album:240", CacheKeyBuilder.BuildLyricsPlusKey("Artist", "Title", "Album", 240));
        Assert.Equal("lyrics:manual-map:Artist:Title", CacheKeyBuilder.BuildLyricsManualMappingKey("Artist", "Title"));
        Assert.Equal("lyrics:id:42", CacheKeyBuilder.BuildLyricsByIdKey(42));

        Assert.Equal("genre:Track:Artist", CacheKeyBuilder.BuildGenreEnrichmentKey("Track", "Artist"));
        Assert.Equal("genre:Track:Artist", CacheKeyBuilder.BuildGenreEnrichmentKey("Track:Artist"));
        Assert.Equal("genre:rock", CacheKeyBuilder.BuildGenreKey("Rock"));
    }

    [Fact]
    public void MusicBrainzAndOdesliKeys_ShouldMatchExpectedFormats()
    {
        Assert.Equal("musicbrainz:isrc:USABC123", CacheKeyBuilder.BuildMusicBrainzIsrcKey("USABC123"));
        Assert.Equal("musicbrainz:search:title:artist:5", CacheKeyBuilder.BuildMusicBrainzSearchKey("Title", "Artist", 5));
        Assert.Equal("musicbrainz:mbid:abc-def", CacheKeyBuilder.BuildMusicBrainzMbidKey("abc-def"));

        Assert.Equal("odesli:tidal-to-spotify:123", CacheKeyBuilder.BuildOdesliTidalToSpotifyKey("123"));
        Assert.Equal("odesli:url-to-spotify:https://example.com/track", CacheKeyBuilder.BuildOdesliUrlToSpotifyKey("https://example.com/track"));
    }
}
