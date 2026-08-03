using System.Globalization;
using System.Text.Json;
using allstarr.Core.Intelligence;

namespace allstarr.Tests;

public sealed class LastFmListeningHistoryImporterTests
{
    [Fact]
    public async Task Scan_DetectsRecentTrackPagesAndPreservesPublicMusicBrainzIdentity()
    {
        var first = Track(
            new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
            "Song A",
            "Artist A",
            "Album A",
            "11111111-1111-1111-1111-111111111111");
        var rows = new object[]
        {
            new Dictionary<string, object?>
            {
                ["track"] = new object[]
                {
                    first,
                    first,
                    Track(new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero), "Song B", "Artist B", null, null),
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Playing now",
                        ["artist"] = Item("Artist"),
                        ["album"] = Item("Album"),
                        ["mbid"] = "",
                        ["@attr"] = new Dictionary<string, object?> { ["nowplaying"] = "true" }
                    },
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Broken",
                        ["date"] = Date(new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero))
                    }
                }
            }
        };
        var accepted = new List<ListeningHistoryImportRow>();
        var registry = new ListeningHistoryImporterRegistry([
            new SpotifyListeningHistoryImporter(),
            new LastFmListeningHistoryImporter()
        ]);

        var scan = await registry.ScanAsync(
            () => Stream(rows),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.Equal("lastfm-recent-tracks", scan.Format);
        Assert.Equal(5, scan.Rows);
        Assert.Equal(2, scan.MusicRows);
        Assert.Equal(2, scan.Completed);
        Assert.Equal(1, scan.Skipped);
        Assert.Equal(1, scan.Malformed);
        Assert.Equal(1, scan.Duplicate);
        Assert.Equal(2, scan.RowsWithoutProviderIdentity);
        Assert.Equal(1, scan.SourceUserCount);
        Assert.Equal(1, scan.EstimatedMusicBrainzLookups);
        Assert.Equal(2, accepted.Count);
        Assert.All(accepted, row => Assert.Equal("lastfm", row.SourceService));
        Assert.Equal("11111111-1111-1111-1111-111111111111", accepted[0].RecordingMusicBrainzId);
        Assert.Null(accepted[1].RecordingMusicBrainzId);
        Assert.Equal(1, scan.ReasonCounts["duplicate"]);
        Assert.Equal(1, scan.ReasonCounts["now_playing"]);
        Assert.Equal(1, scan.ReasonCounts["missing_track_metadata"]);
    }

    [Fact]
    public async Task Scan_EnforcesTheTrackRowLimit()
    {
        var rows = new[]
        {
            new Dictionary<string, object?>
            {
                ["track"] = new[]
                {
                    Track(new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), "One", "Artist", null, null),
                    Track(new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero), "Two", "Artist", null, null)
                }
            }
        };

        var exception = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            new LastFmListeningHistoryImporter().ScanAsync(Stream(rows), new(DateTimeOffset.UtcNow, MaximumRows: 1)));

        Assert.Equal("history_import_row_limit", exception.Code);
    }

    private static Dictionary<string, object?> Track(
        DateTimeOffset listenedAt,
        string title,
        string artist,
        string? album,
        string? recordingMbid) => new()
        {
            ["name"] = title,
            ["artist"] = Item(artist),
            ["album"] = Item(album),
            ["mbid"] = recordingMbid ?? "",
            ["date"] = Date(listenedAt)
        };

    private static Dictionary<string, object?> Item(string? text) => new()
    {
        ["#text"] = text ?? "",
        ["mbid"] = ""
    };

    private static Dictionary<string, object?> Date(DateTimeOffset listenedAt) => new()
    {
        ["uts"] = listenedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        ["#text"] = listenedAt.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture)
    };

    private static Stream Stream(object value) =>
        new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value));
}
