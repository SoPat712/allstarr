using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Intelligence;

public sealed class MalojaListeningHistoryImporter : IListeningHistoryImporter
{
    public const string ImporterRevision = "maloja-export-v1";
    private static readonly DateTimeOffset MinimumTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int MaximumArtists = 64;

    public string Format => "maloja-export";
    public string Revision => ImporterRevision;

    public async Task<ListeningHistoryImportScan?> ScanAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow = null,
        CancellationToken cancellationToken = default)
    {
        if (context.MaximumRows is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(context), "MaximumRows must be between 1 and 10000000.");
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                source,
                new JsonDocumentOptions { MaxDepth = 16 },
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("scrobbles", out var scrobbles) || scrobbles.ValueKind != JsonValueKind.Array)
                return null;
            var state = new ScanState();
            foreach (var element in scrobbles.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                state.Rows++;
                if (state.Rows > context.MaximumRows)
                    throw new ListeningHistoryImportException(
                        "history_import_row_limit",
                        $"The history file exceeds the {context.MaximumRows} row limit.");
                var row = Parse(element, state.Rows, context.Now, state);
                if (row == null) continue;
                var occurrence = Hash($"{row.SourceUserKey}\u001f{row.ListenedAt.ToUnixTimeSeconds()}\u001f{row.SourceItemKey}");
                if (!state.Occurrences.Add(occurrence))
                {
                    state.Duplicate++;
                    AddReason(state.Reasons, "duplicate");
                    continue;
                }
                state.MusicRows++;
                state.Completed++;
                state.LookupKeys.Add(row.SourceItemKey);
                state.Earliest = state.Earliest == null || row.ListenedAt < state.Earliest ? row.ListenedAt : state.Earliest;
                state.Latest = state.Latest == null || row.ListenedAt > state.Latest ? row.ListenedAt : state.Latest;
                AddReason(state.Reasons, row.ReasonCode);
                if (onRow != null) await onRow(row, cancellationToken);
            }
            return new(
                Format,
                state.Rows,
                state.MusicRows,
                state.Completed,
                0,
                0,
                0,
                0,
                state.Malformed,
                state.Duplicate,
                state.MusicRows,
                state.MusicRows > 0 ? 1 : 0,
                state.LookupKeys.Count,
                state.Earliest,
                state.Latest,
                state.Reasons);
        }
    }

    private static ListeningHistoryImportRow? Parse(
        JsonElement element,
        long sequence,
        DateTimeOffset now,
        ScanState state)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !TryTimestamp(element, "time", out var listenedAt) ||
            listenedAt < MinimumTimestamp || listenedAt > now.AddDays(1) ||
            !element.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object ||
            !TryString(track, "title", 500, out var title) || title == null ||
            !track.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array ||
            artists.GetArrayLength() is < 1 or > MaximumArtists ||
            !TryPrimaryArtist(artists, out var artist) || artist == null)
            return Malformed(state, "scrobble_invalid");
        string? album = null;
        if (track.TryGetProperty("album", out var albumElement) && albumElement.ValueKind != JsonValueKind.Null &&
            (albumElement.ValueKind != JsonValueKind.Object || !TryString(albumElement, "albumtitle", 500, out album)))
            return Malformed(state, "album_invalid");
        var sourceItemKey = Hash($"{title.ToUpperInvariant()}\u001f{artist.ToUpperInvariant()}\u001f{album?.ToUpperInvariant()}");
        return new(
            sequence,
            "maloja",
            Hash("maloja-export"),
            sourceItemKey,
            listenedAt,
            listenedAt,
            0,
            null,
            title,
            artist,
            album,
            null,
            null,
            "Maloja",
            null,
            null,
            false,
            null,
            false,
            ListeningHistoryImportClassification.Completed,
            "maloja_scrobble");
    }

    private static bool TryPrimaryArtist(JsonElement artists, out string? value)
    {
        value = null;
        var names = new List<string>(artists.GetArrayLength());
        foreach (var element in artists.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) return false;
            var name = element.GetString()?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 500 || name.Any(char.IsControl)) return false;
            names.Add(name);
        }
        var primary = names.FirstOrDefault(name => name.Contains(" • ", StringComparison.Ordinal)) ?? names[0];
        value = primary.Split(" • ", 2, StringSplitOptions.TrimEntries)[0];
        return value.Length > 0;
    }

    private static bool TryTimestamp(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var seconds)) return false;
        try
        {
            value = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryString(JsonElement element, string name, int maximum, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()?.Trim();
        if (value?.Length == 0) value = null;
        return value == null || value.Length <= maximum && !value.Any(char.IsControl);
    }

    private static ListeningHistoryImportRow? Malformed(ScanState state, string reason)
    {
        state.Malformed++;
        AddReason(state.Reasons, reason);
        return null;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AddReason(IDictionary<string, long> reasons, string reason)
    {
        reasons.TryGetValue(reason, out var current);
        reasons[reason] = current + 1;
    }

    private sealed class ScanState
    {
        public long Rows { get; set; }
        public long MusicRows { get; set; }
        public long Completed { get; set; }
        public long Malformed { get; set; }
        public long Duplicate { get; set; }
        public DateTimeOffset? Earliest { get; set; }
        public DateTimeOffset? Latest { get; set; }
        public HashSet<string> Occurrences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> LookupKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Reasons { get; } = new(StringComparer.Ordinal);
    }
}
