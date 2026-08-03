using System.IO.Compression;
using System.Text;
using System.Text.Json;
using allstarr.Core.Intelligence;

namespace allstarr.Tests;

public sealed class ListenBrainzListeningHistoryImporterTests
{
    [Fact]
    public async Task Scan_ReadsJsonLinesAndPreservesDurationClientAndRecordingIdentity()
    {
        var first = Listen(
            new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
            "Song A",
            "Artist A",
            "Album A",
            "11111111-1111-1111-1111-111111111111",
            180_000,
            "Navidrome");
        var lines = Lines(
            first,
            first,
            Listen(new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero), "Song B", "Artist B", null,
                "22222222-2222-2222-2222-222222222222", 200_000, "Jellyfin"),
            new Dictionary<string, object?>
            {
                ["listened_at"] = "invalid",
                ["track_metadata"] = new Dictionary<string, object?> { ["track_name"] = "Broken", ["artist_name"] = "Artist" }
            });
        var accepted = new List<ListeningHistoryImportRow>();

        var scan = await new ListenBrainzListeningHistoryImporter().ScanAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(lines)),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.NotNull(scan);
        Assert.Equal("listenbrainz-export", scan.Format);
        Assert.Equal(4, scan.Rows);
        Assert.Equal(2, scan.MusicRows);
        Assert.Equal(2, scan.Completed);
        Assert.Equal(1, scan.Malformed);
        Assert.Equal(1, scan.Duplicate);
        Assert.Equal(0, scan.EstimatedMusicBrainzLookups);
        Assert.Equal(2, accepted.Count);
        Assert.All(accepted, row => Assert.Equal("listenbrainz", row.SourceService));
        Assert.Equal(180_000, accepted[0].DurationMilliseconds);
        Assert.Equal("Navidrome", accepted[0].Client);
        Assert.Equal("11111111-1111-1111-1111-111111111111", accepted[0].RecordingMusicBrainzId);
        Assert.Equal(1, scan.ReasonCounts["duplicate"]);
        Assert.Equal(1, scan.ReasonCounts["timestamp_invalid"]);
    }

    [Fact]
    public async Task RegistryReadsBoundedListenBrainzArchivesWithoutUsingTheirFilename()
    {
        var archive = Archive(("metadata.json", "{}"), ("listens/2026.jsonl", Lines(
            Listen(new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), "Song", "Artist", "Album", null, 0, null))));
        var registry = new ListeningHistoryImporterRegistry([
            new SpotifyListeningHistoryImporter(),
            new LastFmListeningHistoryImporter(),
            new ListenBrainzListeningHistoryImporter()
        ]);

        var scan = await registry.ScanAsync(
            () => new MemoryStream(archive),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("listenbrainz-export", scan.Format);
        Assert.Equal(1, scan.MusicRows);
        Assert.Equal(1, scan.EstimatedMusicBrainzLookups);
        Assert.Equal(ListenBrainzListeningHistoryImporter.ImporterRevision,
            registry.RevisionFor("listenbrainz-export"));
    }

    [Fact]
    public async Task Scan_RejectsTraversalNestedArchivesAndExtremeCompression()
    {
        var parser = new ListenBrainzListeningHistoryImporter();
        foreach (var archive in new[]
                 {
                     Archive(("listens/../outside.jsonl", "{}")),
                     Archive(("listens/nested.zip", "not a zip")),
                     Archive(("listens/bomb.jsonl", new string('a', 2 * 1024 * 1024))),
                     SymlinkArchive()
                 })
        {
            var exception = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
                parser.ScanAsync(
                    new MemoryStream(archive),
                    new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero))));
            Assert.Equal("history_import_archive_invalid", exception.Code);
        }
    }

    private static Dictionary<string, object?> Listen(
        DateTimeOffset listenedAt,
        string title,
        string artist,
        string? album,
        string? recordingMbid,
        long durationMilliseconds,
        string? client) => new()
        {
            ["listened_at"] = listenedAt.ToUnixTimeSeconds(),
            ["recording_msid"] = Guid.NewGuid().ToString("D"),
            ["track_metadata"] = new Dictionary<string, object?>
            {
                ["track_name"] = title,
                ["artist_name"] = artist,
                ["release_name"] = album,
                ["additional_info"] = new Dictionary<string, object?>
                {
                    ["recording_mbid"] = recordingMbid,
                    ["duration_ms"] = durationMilliseconds,
                    ["media_player"] = client
                }
            }
        };

    private static string Lines(params object[] rows) =>
        string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row)));

    private static byte[] Archive(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static byte[] SymlinkArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("listens/link.jsonl");
            entry.ExternalAttributes = unchecked((int)0xA0000000);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("outside.jsonl");
        }
        return stream.ToArray();
    }
}
