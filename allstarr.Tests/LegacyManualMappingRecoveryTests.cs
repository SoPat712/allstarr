using allstarr.Models.Admin;
using allstarr.Models.Spotify;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class LegacyManualMappingRecoveryTests
{
    [Fact]
    public void ExactPeerIdentity_RecoversLocalTargetAndPreservesDecisionDate()
    {
        var created = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var legacy = Legacy("same-id", created);
        var peer = new ManualMappingEntry { SpotifyId = "same-id", JellyfinId = "local-1" };

        var recovered = LegacyManualMappingRecovery.TryCreateReplacement(legacy, peer, null, out var result);

        Assert.True(recovered);
        Assert.Equal("local-1", result.JellyfinId);
        Assert.Null(result.ExternalProvider);
        Assert.Equal(created, result.CreatedAt);
    }

    [Fact]
    public void ExactCanonicalIdentity_RecoversPlayableExternalTarget()
    {
        var legacy = Legacy("same-id", DateTime.UtcNow);
        var canonical = new SpotifyTrackMapping
        {
            SpotifyId = "same-id",
            TargetType = "external",
            ExternalProvider = "deezer",
            ExternalId = "track-2",
            Source = "auto"
        };

        var recovered = LegacyManualMappingRecovery.TryCreateReplacement(legacy, null, canonical, out var result);

        Assert.True(recovered);
        Assert.Equal("deezer", result.ExternalProvider);
        Assert.Equal("track-2", result.ExternalId);
    }

    [Fact]
    public void DifferentOrUnavailableIdentity_IsNeverRebound()
    {
        var legacy = Legacy("wanted", DateTime.UtcNow);
        var peer = new ManualMappingEntry { SpotifyId = "different", JellyfinId = "local-1" };
        var canonical = new SpotifyTrackMapping
        {
            SpotifyId = "different",
            TargetType = "external",
            ExternalProvider = "deezer",
            ExternalId = "track-2",
            Source = "auto"
        };

        Assert.False(LegacyManualMappingRecovery.TryCreateReplacement(legacy, peer, canonical, out var result));
        Assert.Same(legacy, result);
    }

    private static ManualMappingEntry Legacy(string spotifyId, DateTime createdAt) => new()
    {
        SpotifyId = spotifyId,
        ExternalProvider = "squidwtf",
        ExternalId = "retired-track",
        CreatedAt = createdAt
    };
}
