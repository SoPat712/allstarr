using allstarr.Core.Music;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class MusicLyricsIdentityTests
{
    [Theory]
    [InlineData("[offset:250]\n[00:01.234]Words", 984)]
    [InlineData("[offset:-250]\n[00:01.234]Words", 1484)]
    [InlineData("[00:01.234]Words\n[offset:+250]", 984)]
    [InlineData("[offset:2000]\n[00:01.234]Words", 0)]
    [InlineData("[offset:invalid]\n[00:01.234]Words", 1234)]
    [InlineData("[offset:9999999999999999999999]\n[00:01.234]Words", 1234)]
    [InlineData("[1000:01.234]Words", 60001234)]
    public void SharedLrcProjectionAppliesGlobalOffsetOnceAndRetainsLongDurations(string lrc, long expected)
    {
        var line = Assert.Single(MusicLyrics.ParseSyncedLines(lrc));
        Assert.Equal(expected, line.StartMilliseconds);
        Assert.Equal("Words", line.Text);
        var song = new Song { Title = "Fixture", Artist = "Artist" };
        var projected = allstarr.Core.Protocols.Subsonic.SubsonicStructuredLyricsMapper.Map(song,
            new allstarr.Models.Lyrics.LyricsInfo { SyncedLyrics = lrc });
        Assert.Equal(0, projected!.OffsetMilliseconds);
        Assert.Equal(expected, Assert.Single(projected.Lines).StartMilliseconds);
        var jellyfin = new allstarr.Core.Protocols.Jellyfin.JellyfinLyricsProtocolAdapter().Shape(
            new allstarr.Models.Lyrics.LyricsInfo { SyncedLyrics = lrc });
        using var document = System.Text.Json.JsonDocument.Parse(jellyfin.Body);
        Assert.Equal(expected * 10_000, document.RootElement.GetProperty("Lyrics")[0].GetProperty("Start").GetInt64());
    }

    [Fact]
    public void LookupRetainsActualRecordingAndSourceIdentityWhileRemovingPresentation()
    {
        var recording = Guid.CreateVersion7();
        var lookup = MusicLyrics.ForLookup(new Song
        {
            Id = "ext-spotify-song-source",
            CanonicalRecordingId = recording,
            ExternalProvider = "spotify",
            ExternalId = "source",
            Isrc = "USRC17607839",
            IsLocal = true,
            LocalPath = "/untrusted/not-authority.flac",
            Title = "⭐ Song [A]",
            Artist = "Artist [E]",
            Artists = ["Artist [E]"],
            Album = "Album [A]",
            Duration = 120,
            PreciseDuration = TimeSpan.FromMilliseconds(120123),
            SpotifyId = "source"
        });
        Assert.Equal(recording, lookup.CanonicalRecordingId);
        Assert.Equal("ext-spotify-song-source", lookup.Id);
        Assert.Equal("spotify", lookup.ExternalProvider);
        Assert.Equal("source", lookup.ExternalId);
        Assert.Equal("USRC17607839", lookup.Isrc);
        Assert.True(lookup.IsLocal);
        Assert.Null(lookup.LocalPath);
        Assert.Equal("Song", lookup.Title);
        Assert.Equal("Artist", lookup.Artist);
        Assert.Equal(["Artist"], lookup.Artists);
        Assert.Equal("Album", lookup.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(120123), lookup.PreciseDuration);
    }
}
