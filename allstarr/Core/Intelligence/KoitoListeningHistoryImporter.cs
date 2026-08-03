using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Intelligence;

public sealed class KoitoListeningHistoryImporter : IListeningHistoryImporter
{
    public const string ImporterRevision = "koito-export-v1";
    private static readonly DateTimeOffset MinimumTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long MaximumDurationSeconds = 24 * 60 * 60;
    private const int MaximumAliases = 64;
    private const int MaximumArtists = 64;

    public string Format => "koito-export-v1";
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
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("listens", out var listens) || listens.ValueKind != JsonValueKind.Array)
                return null;
            if (version.GetString() != "1")
                throw new ListeningHistoryImportException(
                    "history_import_version_unsupported",
                    "This Koito export version is not supported.");
            if (!TryString(root, "user", 200, out var user))
                throw new ListeningHistoryImportException(
                    "history_import_schema_invalid",
                    "The Koito export user is invalid.");

            var state = new ScanState();
            var sourceUserKey = Hash(user ?? "koito-export");
            foreach (var element in listens.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                state.Rows++;
                if (state.Rows > context.MaximumRows)
                    throw new ListeningHistoryImportException(
                        "history_import_row_limit",
                        $"The history file exceeds the {context.MaximumRows} row limit.");
                var row = Parse(element, state.Rows, sourceUserKey, context.Now, state);
                if (row == null) continue;
                var occurrence = Hash($"{row.SourceUserKey}\u001f{row.ListenedAt.ToUnixTimeMilliseconds()}\u001f{row.SourceItemKey}");
                if (!state.Occurrences.Add(occurrence))
                {
                    state.Duplicate++;
                    AddReason(state.Reasons, "duplicate");
                    continue;
                }
                state.MusicRows++;
                state.Completed++;
                state.Earliest = state.Earliest == null || row.ListenedAt < state.Earliest ? row.ListenedAt : state.Earliest;
                state.Latest = state.Latest == null || row.ListenedAt > state.Latest ? row.ListenedAt : state.Latest;
                if (row.RecordingMusicBrainzId == null) state.LookupKeys.Add(row.SourceItemKey);
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
        string sourceUserKey,
        DateTimeOffset now,
        ScanState state)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !TryTimestamp(element, "listened_at", out var listenedAt) ||
            listenedAt < MinimumTimestamp || listenedAt > now.AddDays(1) ||
            !TryString(element, "client", 200, out var client) ||
            !element.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array ||
            artists.GetArrayLength() is < 1 or > MaximumArtists)
            return Malformed(state, "listen_invalid");
        if (!TryPrimaryAlias(track, out var title) ||
            !TryPrimaryArtist(artists, out var artist) || title == null || artist == null)
            return Malformed(state, "missing_track_metadata");
        string? album = null;
        if (element.TryGetProperty("album", out var albumElement) && albumElement.ValueKind != JsonValueKind.Null &&
            (albumElement.ValueKind != JsonValueKind.Object || !TryPrimaryAlias(albumElement, out album)))
            return Malformed(state, "album_invalid");
        if (!TryString(track, "mbid", 36, out var rawMbid) ||
            !track.TryGetProperty("duration", out var duration) || duration.ValueKind != JsonValueKind.Number ||
            !duration.TryGetInt64(out var durationSeconds) || durationSeconds is < 0 or > MaximumDurationSeconds)
            return Malformed(state, "track_invalid");

        var recordingMbid = Guid.TryParse(rawMbid, out var parsedMbid) ? parsedMbid.ToString("D") : null;
        var sourceItemKey = recordingMbid ?? Hash($"{title.ToUpperInvariant()}\u001f{artist.ToUpperInvariant()}\u001f{album?.ToUpperInvariant()}");
        return new(
            sequence,
            "koito",
            sourceUserKey,
            sourceItemKey,
            listenedAt,
            listenedAt,
            0,
            durationSeconds * 1000,
            title,
            artist,
            album,
            null,
            recordingMbid,
            client ?? "Koito",
            null,
            null,
            false,
            null,
            false,
            ListeningHistoryImportClassification.Completed,
            "koito_scrobble");
    }

    private static bool TryPrimaryArtist(JsonElement artists, out string? value)
    {
        value = null;
        JsonElement? fallback = null;
        foreach (var artist in artists.EnumerateArray())
        {
            if (artist.ValueKind != JsonValueKind.Object) return false;
            fallback ??= artist;
            if (artist.TryGetProperty("is_primary", out var primary) && primary.ValueKind == JsonValueKind.True)
            {
                return TryPrimaryAlias(artist, out value);
            }
        }
        return fallback.HasValue && TryPrimaryAlias(fallback.Value, out value);
    }

    private static bool TryPrimaryAlias(JsonElement item, out string? value)
    {
        value = null;
        if (!item.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Array ||
            aliases.GetArrayLength() is < 1 or > MaximumAliases)
            return false;
        string? fallback = null;
        foreach (var alias in aliases.EnumerateArray())
        {
            if (alias.ValueKind != JsonValueKind.Object || !TryString(alias, "alias", 500, out var text) || text == null)
                return false;
            fallback ??= text;
            if (alias.TryGetProperty("is_primary", out var primary) && primary.ValueKind == JsonValueKind.True)
            {
                value = text;
                return true;
            }
        }
        value = fallback;
        return true;
    }

    private static bool TryTimestamp(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParseExact(
                   property.GetString(),
                   ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out value);
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
