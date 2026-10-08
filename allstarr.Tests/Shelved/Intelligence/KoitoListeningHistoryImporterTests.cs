using System.Text.Json;
using allstarr.Core.Intelligence;

namespace allstarr.Tests;

public sealed class KoitoListeningHistoryImporterTests
{
    [Fact]
    public async Task Scan_ReadsVersionedExportAndKeepsOnlyBoundedPublicListenFacts()
    {
        var first = Listen(
            new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
            "Song A",
            "Artist A",
            "Album A",
            "11111111-1111-1111-1111-111111111111",
            354,
            "Koito client");
        first["listened_at"] = "2026-07-01T12:00:00Z";
        var root = new Dictionary<string, object?>
        {
            ["version"] = "1",
            ["exported_at"] = "2026-08-01T00:00:00Z",
            ["user"] = "private-user",
            ["listens"] = new object[]
            {
                first,
                first,
                Listen(new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero), "Song B", "Artist B", null, null, 200, null),
                new Dictionary<string, object?>
                {
                    ["listened_at"] = "2026-07-03T12:00:00Z",
                    ["client"] = "Koito",
                    ["track"] = new Dictionary<string, object?> { ["duration"] = "invalid", ["aliases"] = Aliases("Broken") },
                    ["artists"] = new[] { Artist("Artist", true) }
                }
            }
        };
        var accepted = new List<ListeningHistoryImportRow>();

        var scan = await new KoitoListeningHistoryImporter().ScanAsync(
            Stream(root),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.NotNull(scan);
        Assert.Equal("koito-export-v1", scan.Format);
        Assert.Equal(4, scan.Rows);
        Assert.Equal(2, scan.MusicRows);
        Assert.Equal(2, scan.Completed);
        Assert.Equal(1, scan.Malformed);
        Assert.Equal(1, scan.Duplicate);
        Assert.Equal(1, scan.EstimatedMusicBrainzLookups);
        Assert.Equal(2, accepted.Count);
        Assert.All(accepted, row => Assert.Equal("koito", row.SourceService));
        Assert.Equal("Song A", accepted[0].Title);
        Assert.Equal("Artist A", accepted[0].Artist);
        Assert.Equal("Album A", accepted[0].Album);
        Assert.Equal(354_000, accepted[0].DurationMilliseconds);
        Assert.Equal("11111111-1111-1111-1111-111111111111", accepted[0].RecordingMusicBrainzId);
        Assert.All(accepted, row => Assert.DoesNotContain("private-user", row.SourceUserKey, StringComparison.Ordinal));
        Assert.Equal(1, scan.ReasonCounts["duplicate"]);
        Assert.Equal(1, scan.ReasonCounts["track_invalid"]);
    }

    [Fact]
    public async Task RegistryPinsKoitoRevisionAndParserRejectsUnknownVersionsAndTooManyRows()
    {
        var parser = new KoitoListeningHistoryImporter();
        var unsupported = await Assert.ThrowsAsync<ListeningHistoryImportException>(() => parser.ScanAsync(
            Stream(new { version = "2", listens = Array.Empty<object>() }),
            new(DateTimeOffset.UtcNow)));
        Assert.Equal("history_import_version_unsupported", unsupported.Code);

        var limited = await Assert.ThrowsAsync<ListeningHistoryImportException>(() => parser.ScanAsync(
            Stream(new
            {
                version = "1",
                user = "user",
                listens = new[]
                {
                    Listen(new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), "One", "Artist", null, null, 100, null),
                    Listen(new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero), "Two", "Artist", null, null, 100, null)
                }
            }),
            new(DateTimeOffset.UtcNow, MaximumRows: 1)));
        Assert.Equal("history_import_row_limit", limited.Code);

        var registry = new ListeningHistoryImporterRegistry([parser]);
        Assert.Equal(KoitoListeningHistoryImporter.ImporterRevision, registry.RevisionFor("koito-export-v1"));
    }

    private static Dictionary<string, object?> Listen(
        DateTimeOffset listenedAt,
        string title,
        string artist,
        string? album,
        string? recordingMbid,
        long durationSeconds,
        string? client) => new()
        {
            ["listened_at"] = listenedAt.ToString("O"),
            ["client"] = client,
            ["track"] = new Dictionary<string, object?>
            {
                ["mbid"] = recordingMbid,
                ["duration"] = durationSeconds,
                ["aliases"] = new object[]
                {
                    new Dictionary<string, object?> { ["alias"] = "Fallback title", ["source"] = "Import", ["is_primary"] = false },
                    new Dictionary<string, object?> { ["alias"] = title, ["source"] = "Canonical", ["is_primary"] = true }
                }
            },
            ["album"] = album == null ? null : new Dictionary<string, object?> { ["aliases"] = Aliases(album) },
            ["artists"] = new object[] { Artist("Fallback artist", false), Artist(artist, true) }
        };

    private static Dictionary<string, object?> Artist(string name, bool primary) => new()
    {
        ["is_primary"] = primary,
        ["aliases"] = Aliases(name),
        ["image_url"] = "https://images.example.invalid/private"
    };

    private static object[] Aliases(string value) =>
    [
        new Dictionary<string, object?> { ["alias"] = value, ["source"] = "Canonical", ["is_primary"] = true }
    ];

    private static Stream Stream(object value) =>
        new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value));
}
