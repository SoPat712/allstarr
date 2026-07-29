using allstarr.Models.Scrobbling;
using Xunit;

namespace allstarr.Tests;

public class PlaybackSessionTests
{
    [Fact]
    public void ShouldScrobble_ExternalTrackStartedFromBeginning_ScrobblesWhenThresholdMet()
    {
        var session = CreateSession(isExternal: true, startPositionSeconds: 0, durationSeconds: 300, playedSeconds: 240);

        Assert.True(session.ShouldScrobble());
    }

    [Fact]
    public void ShouldScrobble_ExternalTrackResumedMidTrack_DoesNotScrobble()
    {
        var session = CreateSession(isExternal: true, startPositionSeconds: 90, durationSeconds: 300, playedSeconds: 240);

        Assert.False(session.ShouldScrobble());
    }

    [Fact]
    public void ShouldScrobble_ExternalTrackAtToleranceBoundary_Scrobbles()
    {
        var session = CreateSession(isExternal: true, startPositionSeconds: 5, durationSeconds: 240, playedSeconds: 120);

        Assert.True(session.ShouldScrobble());
    }

    [Fact]
    public void ShouldScrobble_LocalTrackIgnoresStartPosition_ScrobblesWhenThresholdMet()
    {
        var session = CreateSession(isExternal: false, startPositionSeconds: 120, durationSeconds: 300, playedSeconds: 150);

        Assert.True(session.ShouldScrobble());
    }

    private static PlaybackSession CreateSession(
        bool isExternal,
        int startPositionSeconds,
        int durationSeconds,
        int playedSeconds)
    {
        return new PlaybackSession
        {
            SessionId = "session-1",
            DeviceId = "device-1",
            StartTime = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            LastPositionSeconds = playedSeconds,
            Track = new ScrobbleTrack
            {
                Title = "Track",
                Artist = "Artist",
                DurationSeconds = durationSeconds,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsExternal = isExternal,
                StartPositionSeconds = startPositionSeconds
            }
        };
    }
}
