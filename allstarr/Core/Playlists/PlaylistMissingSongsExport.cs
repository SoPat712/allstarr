using System.Globalization;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Playlists;

public sealed record PlaylistExportDocument(byte[] Content, string ContentType, string FileName);

public static class PlaylistMissingSongsExport
{
    public static PlaylistExportDocument Create(DurablePlaylistProjection projection, string format)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        format = format.Trim().ToLowerInvariant();
        if (format is not ("csv" or "json")) throw new ArgumentException("Export format must be csv or json.", nameof(format));
        var sourceByPosition = projection.SourceEntries.ToDictionary(item => item.Position);
        var rows = projection.Entries.Where(item => item.RouteKind is not ("local" or "external"))
            .OrderBy(item => item.Position)
            .Select(item =>
            {
                sourceByPosition.TryGetValue(item.Position, out var source);
                return new MissingSong(item.Position + 1, projection.SourceProviderId,
                    source?.ExternalSnapshotId == item.ExternalSnapshotId ? source.Identity.ExternalId : null,
                    item.Title, item.Artists, item.Album, item.Isrc, item.DurationMilliseconds,
                    item.MatchState?.ToString().ToLowerInvariant());
            }).ToArray();
        if (format == "json")
            return new(JsonSerializer.SerializeToUtf8Bytes(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { WriteIndented = true }), "application/json; charset=utf-8", "missing-songs.json");
        var csv = new StringBuilder("position,sourceProviderId,sourceTrackId,title,artists,album,isrc,durationMilliseconds,matchState\r\n");
        foreach (var row in rows)
            csv.Append(string.Join(',', new[]
            {
                row.Position.ToString(CultureInfo.InvariantCulture), row.SourceProviderId, row.SourceTrackId,
                row.Title, string.Join("; ", row.Artists), row.Album, row.Isrc,
                row.DurationMilliseconds?.ToString(CultureInfo.InvariantCulture), row.MatchState
            }.Select(CsvCell))).Append("\r\n");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return new([.. encoding.GetPreamble(), .. encoding.GetBytes(csv.ToString())],
            "text/csv; charset=utf-8", "missing-songs.csv");
    }

    private static string CsvCell(string? value)
    {
        value ??= "";
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record MissingSong(int Position, string SourceProviderId, string? SourceTrackId,
        string Title, IReadOnlyList<string> Artists, string? Album, string? Isrc,
        long? DurationMilliseconds, string? MatchState);
}
