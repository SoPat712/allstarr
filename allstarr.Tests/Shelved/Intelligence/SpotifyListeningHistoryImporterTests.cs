using System.Buffers.Binary;
using System.IO.Compression;
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

    [Fact]
    public async Task Scan_ZipImportsOnlyAudioHistoryAndSeparatesMusicOutcomes()
    {
        var completed = Track("2026-07-01T12:00:00Z", "Completed", "Artist", null, 180_000, "trackdone");
        var skipped = Track("2026-07-02T12:00:00Z", "Skipped", "Artist", null, 10_000, "forwardbtn", skipped: true);
        var shortPlay = Track("2026-07-03T12:00:00Z", "Short", "Artist", null, 20_000, "endplay");
        var episode = new Dictionary<string, object?>
        {
            ["ts"] = "2026-07-04T12:00:00Z",
            ["ms_played"] = 50_000,
            ["episode_name"] = "Podcast"
        };
        var audiobook = Track("2026-07-05T12:00:00Z", "Chapter", "Author", null, 300_000, "trackdone");
        audiobook["audiobook_title"] = "Book";
        audiobook["audiobook_chapter_uri"] = "spotify:chapter:3333333333333333333333";
        using var archive = Archive(
            ("Spotify Extended Streaming History/Streaming_History_Audio_2026_0.json", new[] { completed, skipped, shortPlay, episode, audiobook }),
            ("Streaming_History_Video_2026_0.json", new[] { completed }),
            ("other.json", new[] { completed }));
        var accepted = new List<ListeningHistoryImportRow>();

        var scan = await new SpotifyListeningHistoryImporter().ScanAsync(
            archive, new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.NotNull(scan);
        Assert.Equal(5, scan.Rows);
        Assert.Equal(3, scan.MusicRows);
        Assert.Equal(1, scan.Completed);
        Assert.Equal(1, scan.Partial);
        Assert.Equal(1, scan.Skipped);
        Assert.Equal(1, scan.Episodes);
        Assert.Equal(1, scan.NonTrack);
        Assert.Equal(1, scan.ReasonCounts["audiobook"]);
        Assert.Equal(new[] { "Completed", "Skipped", "Short" }, accepted.Select(row => row.Title));
        Assert.Equal(new[] { 1L, 2L, 3L }, accepted.Select(row => row.Sequence));
        Assert.True(archive.CanRead);
    }

    [Fact]
    public async Task Scan_ZipUsesDeterministicGlobalSequencesAndDeduplicatesAcrossEntries()
    {
        var first = Track("2026-07-01T12:00:00Z", "First", "Artist", null, 180_000, "trackdone");
        var second = Track("2026-07-02T12:00:00Z", "Second", "Artist", null, 180_000, "trackdone");
        var third = Track("2026-07-03T12:00:00Z", "Third", "Artist", null, 180_000, "trackdone");
        using var archive = Archive(
            ("Streaming_History_Audio_2026_1.json", new[] { first, third }),
            ("Streaming_History_Audio_2026_0.json", new[] { first, second }));
        var accepted = new List<ListeningHistoryImportRow>();

        var scan = await new SpotifyListeningHistoryImporter().ScanAsync(
            archive, new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.NotNull(scan);
        Assert.Equal(4, scan.Rows);
        Assert.Equal(3, scan.MusicRows);
        Assert.Equal(1, scan.Duplicate);
        Assert.Equal(new[] { "First", "Second", "Third" }, accepted.Select(row => row.Title));
        Assert.Equal(new[] { 1L, 2L, 4L }, accepted.Select(row => row.Sequence));
    }

    [Fact]
    public async Task Scan_ZipEnforcesOneRowLimitAcrossEntries()
    {
        var first = Track("2026-07-01T12:00:00Z", "First", "Artist", null, 180_000, "trackdone");
        var second = Track("2026-07-02T12:00:00Z", "Second", "Artist", null, 180_000, "trackdone");
        using var archive = Archive(
            ("Streaming_History_Audio_2026_0.json", new[] { first }),
            ("Streaming_History_Audio_2026_1.json", new[] { second }));
        var accepted = new List<ListeningHistoryImportRow>();

        var exception = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            new SpotifyListeningHistoryImporter().ScanAsync(
                archive, new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), MaximumRows: 1),
                (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; }));

        Assert.Equal("history_import_row_limit", exception.Code);
        Assert.Single(accepted);
    }

    [Fact]
    public async Task Scan_ZipRejectsAggregateExpandedSizeBeforeEmittingRows()
    {
        using var archive = Archive(
            ("Streaming_History_Audio_2026_0.json", Array.Empty<object>()),
            ("Streaming_History_Audio_2026_1.json", Array.Empty<object>()));
        var bytes = archive.ToArray();
        var entries = 0;
        for (var index = 0; index <= bytes.Length - 46; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) != 0x02014b50) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 24, 4), 600 * 1024 * 1024);
            entries++;
        }
        Assert.Equal(2, entries);
        using var oversized = new MemoryStream(bytes);
        var emitted = 0;

        var exception = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            new SpotifyListeningHistoryImporter().ScanAsync(
                oversized, new(DateTimeOffset.UtcNow),
                (_, _) => { emitted++; return ValueTask.CompletedTask; }));

        Assert.Equal("history_import_archive_limit", exception.Code);
        Assert.Equal(0, emitted);
    }

    [Fact]
    public async Task Scan_ZipRejectsInvalidArchiveAndInvalidAudioJson()
    {
        using var invalidArchive = new MemoryStream(new byte[] { 0x50, 0x4b, 0x03, 0x04, 0 });
        var invalid = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            new SpotifyListeningHistoryImporter().ScanAsync(invalidArchive, new(DateTimeOffset.UtcNow)));
        Assert.Equal("history_import_archive_invalid", invalid.Code);

        using var invalidJson = new MemoryStream();
        using (var archive = new ZipArchive(invalidJson, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("Streaming_History_Audio_2026_0.json").Open()))
            writer.Write("[broken");
        invalidJson.Position = 0;
        var malformed = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            new SpotifyListeningHistoryImporter().ScanAsync(invalidJson, new(DateTimeOffset.UtcNow)));
        Assert.Equal("history_import_json_invalid", malformed.Code);
    }

    [Fact]
    public async Task Scan_ZipRejectsTooManyEntries()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < 1025; index++)
                archive.CreateEntry($"other_{index}.json");
        }
        stream.Position = 0;

        var exception = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            new SpotifyListeningHistoryImporter().ScanAsync(stream, new(DateTimeOffset.UtcNow)));

        Assert.Equal("history_import_archive_limit", exception.Code);
    }

    [Fact]
    public async Task Scan_ZipWithoutAudioHistoryIsUnrecognized()
    {
        using var archive = Archive(("Streaming_History_Video_2026_0.json", Array.Empty<object>()));

        var scan = await new SpotifyListeningHistoryImporter().ScanAsync(archive, new(DateTimeOffset.UtcNow));

        Assert.Null(scan);
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

    private static MemoryStream Archive(params (string Name, object Rows)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var entry = archive.CreateEntry(file.Name).Open();
                JsonSerializer.Serialize(entry, file.Rows);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
