using System.IO.Compression;
using System.Text;
using allstarr.Core.Intelligence;

namespace allstarr.Tests;

public sealed class AppleMusicListeningHistoryImporterTests
{
    private const string PlayHeader = "\"Album Name\",\"Apple ID Number\",\"Device OS Name\",\"End Position In Milliseconds\",\"End Reason Type\"," +
        "\"Event End Timestamp\",\"Event Start Timestamp\",\"Event Type\",\"Media Duration In Milliseconds\",\"Media Type\"," +
        "\"Play Duration Milliseconds\",\"Song Name\",\"Start Position In Milliseconds\"";

    private static string Play(string album, string end, string started, string ended, long duration, long played, string song,
        long start = 0, string type = "PLAY_END", string media = "AUDIO") =>
        $"\"{album.Replace("\"", "\"\"")}\",\"12345\",\"iOS\",\"{start + played}\",\"{end}\",\"{ended}\",\"{started}\",\"{type}\"," +
        $"\"{duration}\",\"{media}\",\"{played}\",\"{song}\",\"{start}\"";

    [Fact]
    public async Task Scan_ReadsTheNestedExportAndTakesArtistsFromTheDailyHistoryAndLibrary()
    {
        var plays = new[]
        {
            PlayHeader,
            // Listened to the end; the daily history names the artist and Apple id for that UTC hour.
            Play("Big, Loud Album", "NATURAL_END_OF_TRACK", "2026-08-15T13:10:00.000Z", "2026-08-15T13:13:00.000Z", 180_000, 180_000, "Pick It Up"),
            // Paused at a minute, resumed from the same spot: one listen.
            Play("Chatter - Single", "PLAYBACK_MANUALLY_PAUSED", "2026-08-15T14:00:00.000Z", "2026-08-15T14:01:00.000Z", 200_000, 60_000, "Chatter"),
            Play("Chatter - Single", "NATURAL_END_OF_TRACK", "2026-08-15T14:30:00.000Z", "2026-08-15T14:32:20.000Z", 200_000, 140_000, "Chatter", start: 60_000),
            // Skipped after ten seconds.
            Play("Glad He's Gone - Single", "TRACK_SKIPPED_FORWARDS", "2026-08-15T15:00:00.000Z", "2026-08-15T15:00:10.000Z", 190_000, 10_000, "Glad He's Gone"),
            // Before the daily history begins: the library list knows the album.
            Play("Circus (Deluxe Version)", "TRACK_SKIPPED_FORWARDS", "2023-09-04T17:37:32.622Z", "2023-09-04T17:40:35.443Z", 216_520, 182_821, "If U Seek Amy"),
            // Nobody knows who sang this one.
            Play("Mystery", "NATURAL_END_OF_TRACK", "2026-08-16T10:00:00.000Z", "2026-08-16T10:03:00.000Z", 180_000, 180_000, "Unknown Song"),
            Play("Big, Loud Album", "", "2026-08-15T13:10:00.000Z", "", 180_000, 0, "Pick It Up", type: "PLAY_START"),
            Play("Big, Loud Album", "", "2026-08-15T13:11:00.000Z", "", 180_000, 0, "Pick It Up", type: "LYRIC_DISPLAY"),
            Play("Clips", "NATURAL_END_OF_TRACK", "2026-08-15T16:00:00.000Z", "2026-08-15T16:03:00.000Z", 180_000, 180_000, "A Video", media: "VIDEO")
        };
        var daily = string.Join("\n",
            "Country,\"Track Identifier\",\"Media type\",\"Date Played\",Hours,\"Play Duration Milliseconds\",\"End Reason Type\",\"Source Type\",\"Play Count\",\"Skip Count\",\"Ignore For Recommendations\",\"Track Reference\",\"Track Description\"",
            "USA,1841167573,AUDIO,20260815,13,180000,NATURAL_END_OF_TRACK,IPHONE,1,0,,1841167573,\"Cardi B & Selena Gomez - Pick It Up\"",
            "USA,1656886012,AUDIO,20260815,\"14, 15\",200000,NATURAL_END_OF_TRACK,IPHONE,1,0,,1656886012,\"Connor Price - Chatter\"",
            "USA,1464659365,AUDIO,20260815,15,10000,TRACK_SKIPPED_FORWARDS,IPHONE,0,1,,1464659365,\"Tove Lo - Glad He's Gone\"");
        var library = """
            [{"Title":"If U Seek Amy","Artist":"Britney Spears","Album":"Circus (Deluxe Version)","Apple Music Track Identifier":299939823}]
            """;
        var accepted = new List<ListeningHistoryImportRow>();

        var scan = await new AppleMusicListeningHistoryImporter().ScanAsync(
            Export(string.Join("\r\n", plays), daily, library),
            new(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.NotNull(scan);
        Assert.Equal("apple-music-play-activity", scan.Format);
        Assert.Equal(7, scan.Rows);
        Assert.Equal(4, scan.MusicRows);
        Assert.Equal(3, scan.Completed);
        Assert.Equal(1, scan.Skipped);
        Assert.Equal(1, scan.NonTrack);
        Assert.Equal(1, scan.Malformed);
        Assert.Equal(1, scan.ReasonCounts["missing_track_metadata"]);
        Assert.Equal(0, scan.RowsWithoutProviderIdentity);
        Assert.Equal(["If U Seek Amy", "Pick It Up", "Chatter", "Glad He's Gone"], accepted.Select(row => row.Title));
        Assert.Equal(accepted.Select(row => row.Sequence).Order(), accepted.Select(row => row.Sequence));
        Assert.All(accepted, row => Assert.Equal("apple-musickit", row.SourceService));

        var amy = accepted[0];
        Assert.Equal(("Britney Spears", "299939823", ListeningHistoryImportClassification.Completed, "half_played"),
            (amy.Artist, amy.ProviderTrackReference, amy.Classification, amy.ReasonCode));
        var pick = accepted[1];
        Assert.Equal(("Cardi B & Selena Gomez", "1841167573", "Big, Loud Album", "track_finished"),
            (pick.Artist, pick.ProviderTrackReference, pick.Album, pick.ReasonCode));
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 13, 13, 0, TimeSpan.Zero), pick.ListenedAt);
        Assert.Equal(180_000, pick.DurationMilliseconds);
        var chatter = accepted[2];
        Assert.Equal(("Connor Price", 200_000L, ListeningHistoryImportClassification.Completed),
            (chatter.Artist, chatter.MillisecondsPlayed, chatter.Classification));
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 14, 32, 20, TimeSpan.Zero), chatter.ListenedAt);
        var skipped = accepted[3];
        Assert.Equal(("Tove Lo", ListeningHistoryImportClassification.Skipped, "skip_button"),
            (skipped.Artist, skipped.Classification, skipped.ReasonCode));
    }

    [Fact]
    public async Task Scan_LeavesOtherZipsToOtherImporters()
    {
        var other = new MemoryStream();
        using (var archive = new ZipArchive(other, ZipArchiveMode.Create, leaveOpen: true))
            await Write(archive, "Streaming_History_Audio_2026.json", "[]");
        other.Position = 0;
        Assert.Null(await new AppleMusicListeningHistoryImporter().ScanAsync(other, new(DateTimeOffset.UtcNow)));
        Assert.Null(await new AppleMusicListeningHistoryImporter().ScanAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("\"Album Name\",\"Song Name\"\n")), new(DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Registry_PicksTheAppleExportAfterSpotifyPassesOnIt()
    {
        var plays = string.Join("\n", PlayHeader,
            Play("Album", "NATURAL_END_OF_TRACK", "2026-08-15T13:10:00.000Z", "2026-08-15T13:13:00.000Z", 180_000, 180_000, "Song"));
        var daily = "\"Track Identifier\",\"Date Played\",Hours,\"Track Description\"\n1,20260815,13,\"Artist - Song\"";
        using var bytes = Export(plays, daily, "[]");
        var copy = bytes.ToArray();
        var registry = new ListeningHistoryImporterRegistry(
            [new SpotifyListeningHistoryImporter(), new AppleMusicListeningHistoryImporter()]);

        var scan = await registry.ScanAsync(() => new MemoryStream(copy), new(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("apple-music-play-activity", scan.Format);
        Assert.Equal(1, scan.Completed);
    }

    // Apple's download: a zip holding Apple_Media_Services.zip, which holds the CSVs and a zipped library list.
    private static MemoryStream Export(string plays, string daily, string library)
    {
        var libraryZip = new MemoryStream();
        using (var archive = new ZipArchive(libraryZip, ZipArchiveMode.Create, leaveOpen: true))
            Write(archive, "Apple Music Library Tracks.json", library).GetAwaiter().GetResult();
        var services = new MemoryStream();
        using (var archive = new ZipArchive(services, ZipArchiveMode.Create, leaveOpen: true))
        {
            const string folder = "Apple_Media_Services/Apple Music Activity/";
            Write(archive, folder + "Apple Music Play Activity.csv", "﻿" + plays).GetAwaiter().GetResult();
            Write(archive, folder + "Apple Music - Play History Daily Tracks.csv", daily).GetAwaiter().GetResult();
            Write(archive, folder + "Apple Music Library Tracks.json.zip", libraryZip.ToArray()).GetAwaiter().GetResult();
            Write(archive, "Apple_Media_Services/Stores Activity/Other Activity/Apple Music Click Activity.csv", "ignored").GetAwaiter().GetResult();
        }
        var download = new MemoryStream();
        using (var archive = new ZipArchive(download, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "Apple Media Services Information Part 1 of 2/Apple_Media_Services.zip", services.ToArray()).GetAwaiter().GetResult();
            Write(archive, "Apple Media Services Information Part 1 of 2/Update and Redownload History/iTunes Match Re-download History.csv", "x").GetAwaiter().GetResult();
        }
        download.Position = 0;
        return download;
    }

    private static Task Write(ZipArchive archive, string name, string text) => Write(archive, name, Encoding.UTF8.GetBytes(text));

    private static async Task Write(ZipArchive archive, string name, byte[] bytes)
    {
        await using var stream = archive.CreateEntry(name).Open();
        await stream.WriteAsync(bytes);
    }
}
