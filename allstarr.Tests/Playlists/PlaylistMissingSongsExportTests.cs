using System.Text;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.VisualBasic.FileIO;

namespace allstarr.Tests;

public sealed class PlaylistMissingSongsExportTests
{
    [Fact]
    public void JsonExportsOnlyMissingSourceRowsAndPreservesDuplicatePositionsWithoutPrivateIdentifiers()
    {
        var projection = Projection([
            Entry(0, "external", TrackMatchState.Accepted, "Playable external"),
            Entry(1, "local", TrackMatchState.Accepted, "Owned local"),
            Entry(2, "unmatched", TrackMatchState.Unresolved, "Missing song"),
            Entry(3, "unmatched", TrackMatchState.Rejected, "Rejected song"),
            Entry(4, "unmatched", null, "Missing song")
        ]);
        var export = PlaylistMissingSongsExport.Create(projection, "json");
        Assert.Equal("application/json; charset=utf-8", export.ContentType);
        Assert.Equal("missing-songs.json", export.FileName);
        using var document = JsonDocument.Parse(export.Content);
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(projection.MissingCount, rows.Length);
        Assert.Equal([3, 4, 5], rows.Select(item => item.GetProperty("position").GetInt32()));
        Assert.Equal(["Missing song", "Rejected song", "Missing song"], rows.Select(item => item.GetProperty("title").GetString()));
        Assert.Equal(["unresolved", "rejected", null], rows.Select(item => item.GetProperty("matchState").GetString()));
        Assert.All(rows, item =>
        {
            Assert.Equal("spotify", item.GetProperty("sourceProviderId").GetString());
            Assert.Equal("source-track", item.GetProperty("sourceTrackId").GetString());
            Assert.Equal(180_000, item.GetProperty("durationMilliseconds").GetInt64());
            Assert.Equal("ISRC123", item.GetProperty("isrc").GetString());
            Assert.Equal(["Zoë", "Artist, with comma"], item.GetProperty("artists").EnumerateArray().Select(artist => artist.GetString()));
            Assert.Equal(new[] { "position", "sourceProviderId", "sourceTrackId", "title", "artists", "album", "isrc", "durationMilliseconds", "matchState" }.Order(),
                item.EnumerateObject().Select(property => property.Name).Order());
        });
        Assert.DoesNotContain(projection.ProviderAccountId!.Value.ToString(), Encoding.UTF8.GetString(export.Content), StringComparison.Ordinal);
    }

    [Fact]
    public void CsvPreservesUnicodeQuotedAndMultilineMetadataAndTreatsFormulaTextAsText()
    {
        var entry = Entry(7, "unmatched", TrackMatchState.Unresolved, " =HYPERLINK(\"unexpected\")") with
        { Album = "Album,\r\n\"Name\"", DurationMilliseconds = null };
        var export = PlaylistMissingSongsExport.Create(Projection([entry]), "csv");
        Assert.Equal("text/csv; charset=utf-8", export.ContentType);
        Assert.Equal("missing-songs.csv", export.FileName);
        using var parser = new TextFieldParser(new MemoryStream(export.Content), Encoding.UTF8, detectEncoding: true);
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;
        Assert.Equal(["position", "sourceProviderId", "sourceTrackId", "title", "artists", "album", "isrc", "durationMilliseconds", "matchState"], parser.ReadFields()!);
        var row = parser.ReadFields()!;
        Assert.Equal("8", row[0]);
        Assert.Equal("spotify", row[1]);
        Assert.Equal("source-track", row[2]);
        Assert.Equal("'" + entry.Title, row[3]);
        Assert.Equal("Zoë; Artist, with comma", row[4]);
        Assert.Equal(entry.Album, row[5]);
        Assert.Equal("ISRC123", row[6]);
        Assert.Equal("", row[7]);
        Assert.Equal("unresolved", row[8]);
        Assert.True(parser.EndOfData);
    }

    [Fact]
    public void EmptyMissingExportIsValidAndUnknownFormatsAreRejected()
    {
        var projection = Projection([Entry(0, "local", TrackMatchState.Accepted, "Owned")]);
        using var document = JsonDocument.Parse(PlaylistMissingSongsExport.Create(projection, "json").Content);
        Assert.Equal(0, document.RootElement.GetArrayLength());
        Assert.Throws<ArgumentException>(() => PlaylistMissingSongsExport.Create(projection, "xml"));
    }

    [Fact]
    public void UnknownSourceIdentityRemainsNullInsteadOfExportingAnInternalHashAsATrackId()
    {
        var projection = Projection([Entry(0, "unmatched", null, "Missing")]) with { SourceEntries = [] };
        using var document = JsonDocument.Parse(PlaylistMissingSongsExport.Create(projection, "json").Content);
        Assert.Equal(JsonValueKind.Null, document.RootElement[0].GetProperty("sourceTrackId").ValueKind);
    }

    private static DurablePlaylistProjection Projection(IReadOnlyList<DurablePlaylistEntryProjection> entries)
    {
        var accountId = Guid.CreateVersion7();
        return new(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, "Playlist", "spotify", "private-playlist",
            accountId, "jellyfin", null, null, DateTimeOffset.UtcNow, null, null, entries)
        {
            SourceEntries = entries.Select(item => new DurablePlaylistSourceEntryProjection(item.Position,
                item.ExternalSnapshotId, new("spotify", accountId, new string('a', 64), "revision", 2, "source-track"),
                new(item.Title, item.Artists, item.Album, item.DurationMilliseconds, Isrc: item.Isrc))).ToArray()
        };
    }

    private static DurablePlaylistEntryProjection Entry(int position, string route, TrackMatchState? state, string title) => new(
        position, Guid.CreateVersion7(), "source-track", title, ["Zoë", "Artist, with comma"], "Album", "ISRC123", 180_000,
        null, null, state, route == "local" ? "backend-private-id" : null, route, route == "external" ? "deezer" : null, []);
}
