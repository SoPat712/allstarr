using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Admin;

namespace allstarr.Tests;

public class PlaylistTrackStatusResolverTests
{
    [Fact]
    public void TryResolveFromMatchedTrack_LocalMatch_ReturnsLocal()
    {
        var matchedBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["1UNWD6R5EOFklUHKZZvww2"] = new MatchedTrack
            {
                SpotifyId = "1UNWD6R5EOFklUHKZZvww2",
                MatchedSong = new Song
                {
                    IsLocal = true
                }
            }
        };

        var resolved = PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
            matchedBySpotifyId,
            "1UNWD6R5EOFklUHKZZvww2",
            out var isLocal,
            out var externalProvider);

        Assert.True(resolved);
        Assert.True(isLocal);
        Assert.Null(externalProvider);
    }

    [Fact]
    public void TryResolveFromMatchedTrack_ExternalMatch_ReturnsProvider()
    {
        var matchedBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["6zSpb8dQRaw0M1dK8PBwQz"] = new MatchedTrack
            {
                SpotifyId = "6zSpb8dQRaw0M1dK8PBwQz",
                MatchedSong = new Song
                {
                    IsLocal = false,
                    ExternalProvider = "squidwtf"
                }
            }
        };

        var resolved = PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
            matchedBySpotifyId,
            "6zspb8dqraw0m1dk8pbwqz",
            out var isLocal,
            out var externalProvider);

        Assert.True(resolved);
        Assert.False(isLocal);
        Assert.Equal("squidwtf", externalProvider);
    }

    [Fact]
    public void TryResolveFromMatchedTrack_NoMatch_ReturnsFalse()
    {
        var matchedBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["abc"] = new MatchedTrack
            {
                SpotifyId = "abc",
                MatchedSong = new Song { IsLocal = true }
            }
        };

        var resolved = PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
            matchedBySpotifyId,
            "def",
            out var isLocal,
            out var externalProvider);

        Assert.False(resolved);
        Assert.Null(isLocal);
        Assert.Null(externalProvider);
    }

    [Fact]
    public void TryResolveFromMatchedTrack_NullMatchedSong_ReturnsFalse()
    {
        var matchedBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["abc"] = new MatchedTrack
            {
                SpotifyId = "abc",
                MatchedSong = null!
            }
        };

        var resolved = PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
            matchedBySpotifyId,
            "abc",
            out var isLocal,
            out var externalProvider);

        Assert.False(resolved);
        Assert.Null(isLocal);
        Assert.Null(externalProvider);
    }
}
