using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using System.Text.Json;

namespace allstarr.Tests;

public class SpotifyPlaylistCountHelperTests
{
    [Fact]
    public void ComputeServedItemCount_UsesExactCachedCount_WhenAvailable()
    {
        var matchedTracks = new List<MatchedTrack>
        {
            new() { MatchedSong = new Song { IsLocal = false } },
            new() { MatchedSong = new Song { IsLocal = false } }
        };

        var count = SpotifyPlaylistCountHelper.ComputeServedItemCount(50, 9, matchedTracks);

        Assert.Equal(50, count);
    }

    [Fact]
    public void ComputeServedItemCount_FallsBackToLocalPlusExternalMatched()
    {
        var matchedTracks = new List<MatchedTrack>
        {
            new() { MatchedSong = new Song { IsLocal = true } },
            new() { MatchedSong = new Song { IsLocal = false } },
            new() { MatchedSong = new Song { IsLocal = false } }
        };

        var count = SpotifyPlaylistCountHelper.ComputeServedItemCount(null, 9, matchedTracks);

        Assert.Equal(11, count);
    }

    [Fact]
    public void CountExternalMatchedTracks_IgnoresLocalMatches()
    {
        var matchedTracks = new List<MatchedTrack>
        {
            new() { MatchedSong = new Song { IsLocal = true } },
            new() { MatchedSong = new Song { IsLocal = false } },
            new() { MatchedSong = new Song { IsLocal = false } }
        };

        Assert.Equal(2, SpotifyPlaylistCountHelper.CountExternalMatchedTracks(matchedTracks));
    }

    [Fact]
    public void SumExternalMatchedRunTimeTicks_IgnoresLocalMatches()
    {
        var matchedTracks = new List<MatchedTrack>
        {
            new() { MatchedSong = new Song { IsLocal = true, Duration = 100 } },
            new() { MatchedSong = new Song { IsLocal = false, Duration = 180 } },
            new() { MatchedSong = new Song { IsLocal = false, Duration = 240 } }
        };

        var runTimeTicks = SpotifyPlaylistCountHelper.SumExternalMatchedRunTimeTicks(matchedTracks);

        Assert.Equal((180L + 240L) * TimeSpan.TicksPerSecond, runTimeTicks);
    }

    [Fact]
    public void SumCachedPlaylistRunTimeTicks_HandlesJsonElementsFromCache()
    {
        var cachedPlaylistItems = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>("""
            [
              { "RunTimeTicks": 1800000000 },
              { "RunTimeTicks": 2400000000 }
            ]
            """)!;

        var runTimeTicks = SpotifyPlaylistCountHelper.SumCachedPlaylistRunTimeTicks(cachedPlaylistItems);

        Assert.Equal(4200000000L, runTimeTicks);
    }

    [Fact]
    public void ComputeServedRunTimeTicks_UsesExactCachedRuntime_WhenAvailable()
    {
        var matchedTracks = new List<MatchedTrack>
        {
            new() { MatchedSong = new Song { IsLocal = false, Duration = 180 } }
        };

        var runTimeTicks = SpotifyPlaylistCountHelper.ComputeServedRunTimeTicks(
            5000000000L,
            900000000L,
            matchedTracks);

        Assert.Equal(5000000000L, runTimeTicks);
    }

    [Fact]
    public void ComputeServedRunTimeTicks_FallsBackToLocalPlusExternalMatched()
    {
        var matchedTracks = new List<MatchedTrack>
        {
            new() { MatchedSong = new Song { IsLocal = true, Duration = 100 } },
            new() { MatchedSong = new Song { IsLocal = false, Duration = 180 } },
            new() { MatchedSong = new Song { IsLocal = false, Duration = 240 } }
        };

        var runTimeTicks = SpotifyPlaylistCountHelper.ComputeServedRunTimeTicks(
            null,
            900000000L,
            matchedTracks);

        Assert.Equal(5100000000L, runTimeTicks);
    }
}
