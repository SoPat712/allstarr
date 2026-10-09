using allstarr.Services.Common;
using Xunit;

namespace allstarr.Tests;

public class CacheKeyBuilderTests
{
    [Fact]
    public void LyricsAndMetadataKeys_ShouldMatchExpectedFormats()
    {
        Assert.StartsWith("lyrics:v2:", CacheKeyBuilder.BuildLyricsKey("Artist", "Title", "Album", 240));
        Assert.Equal("lyrics:id:v2:42", CacheKeyBuilder.BuildLyricsByIdKey(42));
        Assert.Equal(
            ApplicationCacheCategory.CanonicalMetadata,
            ApplicationCachePolicyRegistry.Classify(CacheKeyBuilder.BuildAlbumKey("qobuz", "42")));
        Assert.Equal(
            ApplicationCacheCategory.CanonicalMetadata,
            ApplicationCachePolicyRegistry.Classify(CacheKeyBuilder.BuildArtistKey("qobuz", "7")));
        Assert.Equal(
            ApplicationCacheCategory.Artwork,
            ApplicationCachePolicyRegistry.Classify(
                CacheKeyBuilder.BuildProviderPlaylistArtworkDescriptorKey("spotify", "mix", "rev")));
    }

    [Fact]
    public void MusicBrainzAndOdesliKeys_ShouldMatchExpectedFormats()
    {
        Assert.Equal(
            "musicbrainz:isrc:v3:musicbrainz:usabc123",
            CacheKeyBuilder.BuildMusicBrainzIsrcKey("USABC123"));
        Assert.StartsWith(
            "musicbrainz:search:v3:musicbrainz:",
            CacheKeyBuilder.BuildMusicBrainzSearchKey("Title", "Artist", 5));
        Assert.Equal(
            "musicbrainz:recording:v3:musicbrainz:abc-def",
            CacheKeyBuilder.BuildMusicBrainzMbidKey("abc-def"));
        Assert.NotEqual(
            CacheKeyBuilder.BuildMusicBrainzMbidKey("abc-def"),
            CacheKeyBuilder.BuildMusicBrainzMbidKey("abc-def", "brainzmash"));

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
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var key = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            userId, accountId, 7, "spotify", "private mix", "signed-cursor", 100);
        var otherUserKey = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            Guid.CreateVersion7(), accountId, 7, "spotify", "private mix", "signed-cursor", 100);

        Assert.StartsWith(
            $"playlist:discovery:v3:{userId:N}:{accountId:N}:7:spotify:",
            key,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private mix", key, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-cursor", key, StringComparison.Ordinal);
        Assert.NotEqual(key, otherUserKey);
        Assert.Equal(
            $"playlist:discovery:v3:*:{accountId:N}:*",
            CacheKeyBuilder.BuildProviderPlaylistDiscoveryAccountPattern(accountId));
        Assert.Equal(
            ApplicationCacheCategory.PlaylistDiscovery,
            ApplicationCachePolicyRegistry.Classify(key));
    }

    [Fact]
    public void PlaybackMissKeys_UseTheNegativeResultPolicy()
    {
        var metadataKey = CacheKeyBuilder.BuildPlaybackMetadataNegativeKey("jellyfin", "track-1");
        var routeKey = CacheKeyBuilder.BuildPlaybackRouteNegativeKey(
            Guid.CreateVersion7(),
            "spotiflac-ytmusic-spotiflac",
            "private-track-id",
            "Lossless");

        Assert.StartsWith("negative:playback:metadata:v1:jellyfin:", metadataKey);
        Assert.DoesNotContain("track-1", metadataKey, StringComparison.Ordinal);
        Assert.Equal(
            ApplicationCacheCategory.NegativeResult,
            ApplicationCachePolicyRegistry.Classify(metadataKey));
        Assert.StartsWith("negative:playback:route:v1:", routeKey);
        Assert.DoesNotContain("private-track-id", routeKey, StringComparison.Ordinal);
        Assert.DoesNotContain("spotiflac", routeKey, StringComparison.Ordinal);
        Assert.Equal(
            ApplicationCacheCategory.NegativeResult,
            ApplicationCachePolicyRegistry.Classify(routeKey));
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
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var key = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            userId,
            accountId,
            "spotify",
            "playlist",
            "private-playlist-id",
            "signed-revision",
            96,
            96));

        Assert.StartsWith(
            $"media:descriptor:v4:{userId:N}:{accountId:N}:spotify:playlist:",
            key,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-playlist-id", key, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-revision", key, StringComparison.Ordinal);
        Assert.Equal(
            $"media:descriptor:v4:*:{accountId:N}:*",
            CacheKeyBuilder.BuildMediaAssetDescriptorAccountPattern(accountId));
    }
}
