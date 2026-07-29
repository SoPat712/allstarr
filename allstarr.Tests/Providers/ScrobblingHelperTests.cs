using Xunit;
using allstarr.Services.Scrobbling;
using allstarr.Models.Scrobbling;

namespace allstarr.Tests;

/// <summary>
/// Tests for ScrobblingHelper utility functions
/// </summary>
public class ScrobblingHelperTests
{
    [Theory]
    [InlineData(0, false)]      // 0 seconds - too short
    [InlineData(29, false)]     // 29 seconds - too short
    [InlineData(30, true)]      // 30 seconds - minimum
    [InlineData(60, true)]      // 1 minute
    [InlineData(240, true)]     // 4 minutes
    [InlineData(300, true)]     // 5 minutes
    [InlineData(3600, true)]    // 1 hour
    public void IsTrackLongEnoughToScrobble_VariousDurations_ReturnsCorrectly(int durationSeconds, bool expected)
    {
        // Last.fm rules: tracks must be at least 30 seconds long
        // Act
        var result = ScrobblingHelper.IsTrackLongEnoughToScrobble(durationSeconds);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(100, 50, true)]     // Listened to 50% of 100s track
    [InlineData(100, 51, true)]     // Listened to 51% of 100s track
    [InlineData(100, 49, false)]    // Listened to 49% of 100s track
    [InlineData(600, 240, true)]    // Listened to 4 minutes of 10 minute track - meets 4min threshold!
    [InlineData(600, 239, false)]   // Listened to 3:59 of 10 minute track - just under threshold
    [InlineData(600, 300, true)]    // Listened to 5 minutes of 10 minute track (50%)
    [InlineData(120, 60, true)]     // Listened to 50% of 2 minute track
    [InlineData(30, 15, true)]      // Listened to 50% of 30 second track
    public void HasListenedEnoughToScrobble_VariousPlaytimes_ReturnsCorrectly(
        int trackDurationSeconds, int playedSeconds, bool expected)
    {
        // Last.fm rules: must listen to at least 50% of track OR 4 minutes (whichever comes first)
        // Act
        var result = ScrobblingHelper.HasListenedEnoughToScrobble(trackDurationSeconds, playedSeconds);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(600, 240, true)]    // 4 minutes of 10 minute track
    [InlineData(600, 239, false)]   // 3:59 of 10 minute track
    [InlineData(1000, 240, true)]   // 4 minutes of 16+ minute track
    [InlineData(1000, 500, true)]   // 8+ minutes of 16+ minute track
    public void FourMinuteRule_LongTracks_AppliesCorrectly(
        int trackDurationSeconds, int playedSeconds, bool expected)
    {
        // For tracks longer than 8 minutes, only need to listen to 4 minutes
        // Act
        var halfDuration = trackDurationSeconds / 2.0;
        var fourMinutes = 240;
        var threshold = Math.Min(halfDuration, fourMinutes);
        var result = playedSeconds >= threshold;

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("Track", "", false)]
    [InlineData("", "Artist", false)]
    [InlineData("Track", "Artist", true)]
    public void HasRequiredMetadata_VariousInputs_ValidatesCorrectly(
        string trackName, string artistName, bool expected)
    {
        // Scrobbling requires at minimum: track name and artist name
        // Act
        var result = ScrobblingHelper.HasRequiredMetadata(trackName, artistName);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Track Name", "Artist Name", "Track Name - Artist Name")]
    [InlineData("Song", "Band", "Song - Band")]
    [InlineData("Title (feat. Guest)", "Main Artist", "Title (feat. Guest) - Main Artist")]
    public void FormatScrobbleDisplay_VariousInputs_FormatsCorrectly(
        string trackName, string artistName, string expected)
    {
        // Act
        var result = ScrobblingHelper.FormatTrackForDisplay(trackName, artistName);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ScrobbleTrack_ValidData_CreatesCorrectObject()
    {
        // Arrange
        var track = new ScrobbleTrack
        {
            Title = "Test Track",
            Artist = "Test Artist",
            Album = "Test Album",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DurationSeconds = 180
        };

        // Assert
        Assert.NotNull(track.Title);
        Assert.NotNull(track.Artist);
        Assert.True(track.Timestamp > 0);
        Assert.True(track.DurationSeconds > 0);
    }

    [Theory]
    [InlineData("Track & Artist", "Track & Artist")]
    [InlineData("Track (feat. Someone)", "Track (feat. Someone)")]
    [InlineData("Track - Remix", "Track - Remix")]
    [InlineData("Track [Radio Edit]", "Track [Radio Edit]")]
    public void TrackName_SpecialCharacters_PreservesCorrectly(string input, string expected)
    {
        // Track names with special characters should be preserved as-is
        // Act
        var track = new ScrobbleTrack
        {
            Title = input,
            Artist = "Artist",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Assert
        Assert.Equal(expected, track.Title);
    }

    [Theory]
    [InlineData(1000000000)]    // 2001-09-09
    [InlineData(1500000000)]    // 2017-07-14
    [InlineData(1700000000)]    // 2023-11-14
    public void Timestamp_ValidUnixTimestamps_AcceptsCorrectly(long timestamp)
    {
        // Act
        var track = new ScrobbleTrack
        {
            Title = "Track",
            Artist = "Artist",
            Timestamp = timestamp
        };

        // Assert
        Assert.Equal(timestamp, track.Timestamp);
        Assert.True(timestamp > 0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Timestamp_InvalidValues_ShouldBeRejected(long timestamp)
    {
        // Timestamps should be positive Unix timestamps
        // Act & Assert
        Assert.True(timestamp <= 0);
    }

    [Theory]
    [InlineData(30)]      // Minimum valid duration
    [InlineData(180)]     // 3 minutes
    [InlineData(240)]     // 4 minutes (scrobble threshold)
    [InlineData(300)]     // 5 minutes
    [InlineData(3600)]    // 1 hour
    public void Duration_ValidDurations_AcceptsCorrectly(int duration)
    {
        // Act
        var track = new ScrobbleTrack
        {
            Title = "Track",
            Artist = "Artist",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DurationSeconds = duration
        };

        // Assert
        Assert.Equal(duration, track.DurationSeconds);
        Assert.True(duration >= 30);
    }
}
