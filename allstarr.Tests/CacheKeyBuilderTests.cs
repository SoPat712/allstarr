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
            "search:data:musicalbum:500:0:efa26829c37196b030fa31d127e0715b:datecreated,sortname:descending:true:1635cd7d23144ba08251ebe22a56119e:",
            key);
    }

    [Fact]
    public void SearchKey_ShouldDifferentiateFavoriteOnlyQueries()
    {
        var normalKey = CacheKeyBuilder.BuildSearchKey(
            "Sunflower",
            "Audio",
            100,
            0,
            "parent",
            "SortName",
            "Ascending",
            true,
            "user-1",
            "false");

        var favoritesOnlyKey = CacheKeyBuilder.BuildSearchKey(
            "Sunflower",
            "Audio",
            100,
            0,
            "parent",
            "SortName",
            "Ascending",
            true,
            "user-1",
            "true");

        Assert.NotEqual(normalKey, favoritesOnlyKey);
        Assert.EndsWith(":false", normalKey);
        Assert.EndsWith(":true", favoritesOnlyKey);
    }

    [Fact]
    public void SearchKey_OldOverload_ShouldRemainCompatible()
    {
        Assert.Equal("search:data:Audio:500:0", CacheKeyBuilder.BuildSearchKey("DATA", "Audio", 500, 0));
    }

    [Fact]
    public void LyricsAndGenreKeys_ShouldMatchExpectedFormats()
    {
        Assert.Equal("lyrics:Artist:Title:Album:240", CacheKeyBuilder.BuildLyricsKey("Artist", "Title", "Album", 240));
        Assert.Equal("lyricsplus:Artist:Title:Album:240", CacheKeyBuilder.BuildLyricsPlusKey("Artist", "Title", "Album", 240));
        Assert.Equal("lyrics:id:42", CacheKeyBuilder.BuildLyricsByIdKey(42));

        Assert.Equal("genre:Track:Artist", CacheKeyBuilder.BuildGenreEnrichmentKey("Track", "Artist"));
        Assert.Equal("genre:Track:Artist", CacheKeyBuilder.BuildGenreEnrichmentKey("Track:Artist"));
        Assert.Equal(
            ApplicationCacheCategory.CanonicalMetadata,
            ApplicationCachePolicyRegistry.Classify(CacheKeyBuilder.BuildAlbumKey("qobuz", "42")));
        Assert.Equal(
            ApplicationCacheCategory.CanonicalMetadata,
            ApplicationCachePolicyRegistry.Classify(CacheKeyBuilder.BuildArtistKey("qobuz", "7")));
        Assert.Equal(
            ApplicationCacheCategory.CanonicalMetadata,
            ApplicationCachePolicyRegistry.Classify(
                CacheKeyBuilder.BuildProviderPlaylistArtworkDescriptorKey("spotify", "mix", "rev")));
    }

    [Fact]
    public void MusicBrainzAndOdesliKeys_ShouldMatchExpectedFormats()
    {
        Assert.Equal("musicbrainz:isrc:USABC123", CacheKeyBuilder.BuildMusicBrainzIsrcKey("USABC123"));
        Assert.Equal("musicbrainz:search:title:artist:5", CacheKeyBuilder.BuildMusicBrainzSearchKey("Title", "Artist", 5));
        Assert.Equal("musicbrainz:mbid:abc-def", CacheKeyBuilder.BuildMusicBrainzMbidKey("abc-def"));

        Assert.Equal("odesli:tidal-to-spotify:123", CacheKeyBuilder.BuildOdesliTidalToSpotifyKey("123"));
        var urlKey = CacheKeyBuilder.BuildOdesliUrlToSpotifyKey("https://example.com/track?token=secret");
        Assert.StartsWith("odesli:url-to-spotify:v2:", urlKey, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", urlKey, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", urlKey, StringComparison.Ordinal);

        var translationKey = CacheKeyBuilder.BuildOdesliTranslationKey(
            "https://example.com/track?token=secret",
            "Deezer");
        Assert.StartsWith("odesli:translate:v2:", translationKey, StringComparison.Ordinal);
        Assert.EndsWith(":deezer", translationKey, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", translationKey, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", translationKey, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistDiscoveryKeys_AreScopedHashedProviderResponses()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var key = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            tenantId, userId, accountId, 7, "spotify", "private mix", "signed-cursor", 100);
        var otherUserKey = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            tenantId, Guid.CreateVersion7(), accountId, 7, "spotify", "private mix", "signed-cursor", 100);

        Assert.StartsWith("playlist:discovery:v1:", key, StringComparison.Ordinal);
        Assert.DoesNotContain("private mix", key, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-cursor", key, StringComparison.Ordinal);
        Assert.NotEqual(key, otherUserKey);
        Assert.Equal(
            ApplicationCacheCategory.PlaylistDiscovery,
            ApplicationCachePolicyRegistry.Classify(key));
    }

    [Fact]
    public void PlaybackMissKeys_UseTheNegativeResultPolicy()
    {
        var key = CacheKeyBuilder.BuildPlaybackMetadataNegativeKey("jellyfin", "track-1");

        Assert.Equal("negative:playback:metadata:jellyfin:track-1", key);
        Assert.Equal(
            ApplicationCacheCategory.NegativeResult,
            ApplicationCachePolicyRegistry.Classify(key));
    }
}
