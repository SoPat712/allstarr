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

        Assert.StartsWith("search:v2:", key, StringComparison.Ordinal);
        Assert.DoesNotContain("data", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1635cd7d", key, StringComparison.OrdinalIgnoreCase);
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
        Assert.StartsWith("search:v2:", normalKey, StringComparison.Ordinal);
        Assert.StartsWith("search:v2:", favoritesOnlyKey, StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsAndGenreKeys_ShouldMatchExpectedFormats()
    {
        Assert.StartsWith("lyrics:v2:", CacheKeyBuilder.BuildLyricsKey("Artist", "Title", "Album", 240));
        Assert.StartsWith("lyricsplus:v2:", CacheKeyBuilder.BuildLyricsPlusKey("Artist", "Title", "Album", 240));
        Assert.Equal("lyrics:id:v2:42", CacheKeyBuilder.BuildLyricsByIdKey(42));
        Assert.StartsWith("genre:v2:", CacheKeyBuilder.BuildGenreEnrichmentKey("Track:Artist"));
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
        Assert.Equal("musicbrainz:isrc:v2:usabc123", CacheKeyBuilder.BuildMusicBrainzIsrcKey("USABC123"));
        Assert.StartsWith("musicbrainz:search:v2:", CacheKeyBuilder.BuildMusicBrainzSearchKey("Title", "Artist", 5));
        Assert.Equal("musicbrainz:mbid:v2:abc-def", CacheKeyBuilder.BuildMusicBrainzMbidKey("abc-def"));

        Assert.StartsWith("odesli:tidal-to-spotify:v2:", CacheKeyBuilder.BuildOdesliTidalToSpotifyKey("123"));
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

        Assert.StartsWith(
            $"playlist:discovery:v2:{tenantId:N}:{userId:N}:{accountId:N}:7:spotify:",
            key,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private mix", key, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-cursor", key, StringComparison.Ordinal);
        Assert.NotEqual(key, otherUserKey);
        Assert.Equal(
            $"playlist:discovery:v2:*:*:{accountId:N}:*",
            CacheKeyBuilder.BuildProviderPlaylistDiscoveryAccountPattern(accountId));
        Assert.Equal(
            ApplicationCacheCategory.PlaylistDiscovery,
            ApplicationCachePolicyRegistry.Classify(key));
    }

    [Fact]
    public void PlaybackMissKeys_UseTheNegativeResultPolicy()
    {
        var key = CacheKeyBuilder.BuildPlaybackMetadataNegativeKey("jellyfin", "track-1");

        Assert.StartsWith("negative:playback:metadata:v1:jellyfin:", key);
        Assert.DoesNotContain("track-1", key, StringComparison.Ordinal);
        Assert.Equal(
            ApplicationCacheCategory.NegativeResult,
            ApplicationCachePolicyRegistry.Classify(key));
    }

    [Fact]
    public void UnknownNamespaces_HaveNoSemanticOwner()
    {
        Assert.False(ApplicationCachePolicyRegistry.TryClassify(
            "abandoned:key",
            out _));
        Assert.True(ApplicationCachePolicyRegistry.TryClassify(
            "odesli:translate:v2:fixture:spotify",
            out var category));
        Assert.Equal(ApplicationCacheCategory.ProviderResponse, category);
        Assert.False(ApplicationCachePolicyRegistry.TryClassify(
            "lyrics:Artist:Title:Album:240",
            out _));
    }

    [Fact]
    public void MediaDescriptorKeys_ExposeOnlyStableOwnershipDimensions()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var key = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            tenantId,
            userId,
            accountId,
            "spotify",
            "playlist",
            "private-playlist-id",
            "signed-revision",
            96,
            96));

        Assert.StartsWith(
            $"media:descriptor:v3:{tenantId:N}:{userId:N}:{accountId:N}:spotify:playlist:",
            key,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-playlist-id", key, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-revision", key, StringComparison.Ordinal);
        Assert.Equal(
            $"media:descriptor:v3:*:*:{accountId:N}:*",
            CacheKeyBuilder.BuildMediaAssetDescriptorAccountPattern(accountId));
    }
}
