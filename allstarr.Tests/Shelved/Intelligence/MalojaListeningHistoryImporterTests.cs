using System.Text.Json;
using allstarr.Core.Intelligence;

namespace allstarr.Tests;

public sealed class MalojaListeningHistoryImporterTests
{
    [Fact]
    public async Task Scan_ReadsScrobblesAndChoosesTheMainArtistFromMalojaCredits()
    {
        var first = Scrobble(
            new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
            "Song A",
            ["Featured Artist", "Main Artist • Featured Artist"],
            "Album A");
        var root = new Dictionary<string, object?>
        {
            ["scrobbles"] = new object[]
            {
                first,
                first,
                Scrobble(new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero), "Song B", ["Artist B"], null),
                new Dictionary<string, object?>
                {
                    ["time"] = "invalid",
                    ["track"] = new Dictionary<string, object?> { ["title"] = "Broken", ["artists"] = new[] { "Artist" } }
                }
            }
        };
        var accepted = new List<ListeningHistoryImportRow>();

        var scan = await new MalojaListeningHistoryImporter().ScanAsync(
            Stream(root),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.NotNull(scan);
        Assert.Equal("maloja-export", scan.Format);
        Assert.Equal(4, scan.Rows);
        Assert.Equal(2, scan.MusicRows);
        Assert.Equal(2, scan.Completed);
        Assert.Equal(1, scan.Malformed);
        Assert.Equal(1, scan.Duplicate);
        Assert.Equal(2, scan.EstimatedMusicBrainzLookups);
        Assert.Equal(2, accepted.Count);
        Assert.All(accepted, row => Assert.Equal("maloja", row.SourceService));
        Assert.Equal("Main Artist", accepted[0].Artist);
        Assert.Equal("Album A", accepted[0].Album);
        Assert.Equal("Maloja", accepted[0].Client);
        Assert.All(accepted, row => Assert.Null(row.RecordingMusicBrainzId));
        Assert.Equal(1, scan.ReasonCounts["duplicate"]);
        Assert.Equal(1, scan.ReasonCounts["scrobble_invalid"]);
    }

    [Fact]
    public async Task RegistryPinsMalojaRevisionAndParserEnforcesTheRowLimit()
    {
        var parser = new MalojaListeningHistoryImporter();
        var limited = await Assert.ThrowsAsync<ListeningHistoryImportException>(() => parser.ScanAsync(
            Stream(new
            {
                scrobbles = new[]
                {
                    Scrobble(new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), "One", ["Artist"], null),
                    Scrobble(new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero), "Two", ["Artist"], null)
                }
            }),
            new(DateTimeOffset.UtcNow, MaximumRows: 1)));
        Assert.Equal("history_import_row_limit", limited.Code);

        var registry = new ListeningHistoryImporterRegistry([parser]);
        Assert.Equal(MalojaListeningHistoryImporter.ImporterRevision, registry.RevisionFor("maloja-export"));
    }

    private static Dictionary<string, object?> Scrobble(
        DateTimeOffset listenedAt,
        string title,
        string[] artists,
        string? album) => new()
        {
            ["time"] = listenedAt.ToUnixTimeSeconds(),
            ["track"] = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["artists"] = artists,
                ["album"] = album == null ? null : new Dictionary<string, object?> { ["albumtitle"] = album }
            }
        };

    private static Stream Stream(object value) =>
        new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value));
}
