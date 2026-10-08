using System.Text;
using System.Text.Json;
using allstarr.Core.Intelligence;

namespace allstarr.Tests;

public sealed class SpotifyListeningHistoryImporterTests
{
    [Fact]
    public async Task Scan_DetectsSchemaAndSeparatesMusicOutcomesWithoutRetainingPrivateNetworkFacts()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Track("2026-07-01T12:00:00Z", "Song A", "Artist A", "spotify:track:1111111111111111111111", 180_000, "trackdone"),
            Track("2026-07-01T12:00:00Z", "Song A", "Artist A", "spotify:track:1111111111111111111111", 180_000, "trackdone"),
            Track("2026-07-02T12:00:00Z", "Song A", "Artist A", "spotify:track:1111111111111111111111", 250_000, "trackdone"),
            Track("2026-07-03T12:00:00Z", "Song B", "Artist B", "spotify:track:2222222222222222222222", 100_000, "endplay"),
            Track("2026-07-04T12:00:00Z", "Song C", "Artist C", null, 10_000, "forwardbtn", skipped: true),
            new()
            {
                ["ts"] = "2026-07-05T12:00:00Z",
                ["username"] = "private-user",
                ["ms_played"] = 50_000,
                ["episode_name"] = "Episode",
                ["spotify_episode_uri"] = "spotify:episode:3333333333333333333333"
            },
            new()
            {
                ["ts"] = "2026-07-06T12:00:00Z",
                ["username"] = "private-user",
                ["ms_played"] = 0,
                ["master_metadata_track_name"] = null,
                ["spotify_track_uri"] = null
            },
            Track("not-a-time", "Broken", "Artist", "spotify:track:4444444444444444444444", 1000, "endplay")
        };
        var accepted = new List<ListeningHistoryImportRow>();

        var scan = await new SpotifyListeningHistoryImporter().ScanAsync(
            Stream(rows),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.NotNull(scan);
        Assert.Equal("spotify-extended-streaming-history", scan.Format);
        Assert.Equal(8, scan.Rows);
        Assert.Equal(4, scan.MusicRows);
        Assert.Equal(2, scan.Completed);
        Assert.Equal(1, scan.Partial);
        Assert.Equal(1, scan.Skipped);
        Assert.Equal(1, scan.Episodes);
        Assert.Equal(1, scan.NonTrack);
        Assert.Equal(1, scan.Malformed);
        Assert.Equal(1, scan.Duplicate);
        Assert.Equal(1, scan.RowsWithoutProviderIdentity);
        Assert.Equal(1, scan.SourceUserCount);
        Assert.Equal(3, scan.EstimatedMusicBrainzLookups);
        Assert.Equal(4, accepted.Count);
        Assert.Equal(2, accepted.Count(row => row.Title == "Song A"));
        Assert.All(accepted, row => Assert.Equal(64, row.SourceUserKey.Length));
        Assert.All(accepted, row => Assert.True(row.Offline));
        Assert.All(accepted, row => Assert.NotNull(row.OfflineAt));
        Assert.DoesNotContain("private-user", JsonSerializer.Serialize(accepted), StringComparison.Ordinal);
        Assert.Equal(1, scan.ReasonCounts["duplicate"]);
        Assert.Equal(1, scan.ReasonCounts["episode"]);
    }

    [Fact]
    public async Task RegistryRejectsUnknownFormatsAndParserEnforcesTheRowLimit()
    {
        var registry = new ListeningHistoryImporterRegistry([new SpotifyListeningHistoryImporter()]);
        Assert.Equal(SpotifyListeningHistoryImporter.ImporterRevision,
            registry.RevisionFor("spotify-extended-streaming-history"));
        var missingRevision = Assert.Throws<ListeningHistoryImportException>(() => registry.RevisionFor("missing"));
        Assert.Equal("history_import_format_unsupported", missingRevision.Code);
        var unknown = Encoding.UTF8.GetBytes("[{\"foo\":1}]");
        var unsupported = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            registry.ScanAsync(() => new MemoryStream(unknown), new(DateTimeOffset.UtcNow)));
        Assert.Equal("history_import_format_unsupported", unsupported.Code);

        var spotify = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            Track("2026-07-01T12:00:00Z", "One", "Artist", null, 1000, "endplay"),
            Track("2026-07-02T12:00:00Z", "Two", "Artist", null, 1000, "endplay")
        });
        var limited = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            registry.ScanAsync(() => new MemoryStream(spotify), new(DateTimeOffset.UtcNow, MaximumRows: 1)));
        Assert.Equal("history_import_row_limit", limited.Code);
    }

    private static Dictionary<string, object?> Track(
        string timestamp,
        string title,
        string artist,
        string? uri,
        long milliseconds,
        string reasonEnd,
        bool skipped = false) => new()
        {
            ["ts"] = timestamp,
            ["username"] = "private-user",
            ["platform"] = "desktop",
            ["ms_played"] = milliseconds,
            ["master_metadata_track_name"] = title,
            ["master_metadata_album_artist_name"] = artist,
            ["master_metadata_album_album_name"] = "Album",
            ["spotify_track_uri"] = uri,
            ["reason_start"] = "trackdone",
            ["reason_end"] = reasonEnd,
            ["skipped"] = skipped,
            ["offline"] = true,
            ["offline_timestamp"] = 1_767_225_600_000L,
            ["incognito_mode"] = true,
            ["ip_addr_decrypted"] = "192.0.2.1",
            ["user_agent_decrypted"] = "private-user-agent"
        };

    private static Stream Stream(object value) =>
        new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value));
}
