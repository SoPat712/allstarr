using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Intelligence;

public sealed class LastFmListeningHistoryImporter : IListeningHistoryImporter
{
    public const string ImporterRevision = "lastfm-recent-tracks-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private static readonly DateTimeOffset MinimumTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string Format => "lastfm-recent-tracks";
    public string Revision => ImporterRevision;

    public async Task<ListeningHistoryImportScan?> ScanAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow = null,
        CancellationToken cancellationToken = default)
    {
        if (context.MaximumRows is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(context), "MaximumRows must be between 1 and 10000000.");
        var state = new ScanState();
        try
        {
            await foreach (var page in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                               source, JsonOptions, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.ValueKind != JsonValueKind.Object ||
                    !page.TryGetProperty("track", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                {
                    if (state.Recognized) Malformed(state, "schema_mismatch_page");
                    continue;
                }
                state.Recognized = true;
                foreach (var element in tracks.EnumerateArray())
                {
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
                    state.RowsWithoutProviderIdentity++;
                    state.Earliest = state.Earliest == null || row.ListenedAt < state.Earliest ? row.ListenedAt : state.Earliest;
                    state.Latest = state.Latest == null || row.ListenedAt > state.Latest ? row.ListenedAt : state.Latest;
                    if (row.RecordingMusicBrainzId == null) state.LookupKeys.Add(row.SourceItemKey);
                    AddReason(state.Reasons, row.ReasonCode);
                    if (onRow != null) await onRow(row, cancellationToken);
                }
            }
        }
        catch (JsonException exception)
        {
            if (!state.Recognized) return null;
            throw new ListeningHistoryImportException(
                "history_import_json_invalid",
                "The Last.fm history file contains invalid JSON.",
                exception);
        }
        if (!state.Recognized) return null;
        return new(
            Format,
            state.Rows,
            state.MusicRows,
            state.Completed,
            0,
            state.Skipped,
            0,
            0,
            state.Malformed,
            state.Duplicate,
            state.RowsWithoutProviderIdentity,
            1,
            state.LookupKeys.Count,
            state.Earliest,
            state.Latest,
            state.Reasons);
    }

    private static ListeningHistoryImportRow? Parse(
        JsonElement element,
        long sequence,
        DateTimeOffset now,
        ScanState state)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !TryString(element, "name", 500, out var title) ||
            !TryItem(element, "artist", 500, out var artist, out _) ||
            !TryItem(element, "album", 500, out var album, out _) ||
            !TryString(element, "mbid", 36, out var rawRecordingMbid))
            return Malformed(state, "malformed_field");
        if (title == null || artist == null) return Malformed(state, "missing_track_metadata");
        if (!TryTimestamp(element, out var listenedAt))
        {
            if (IsNowPlaying(element))
            {
                state.Skipped++;
                AddReason(state.Reasons, "now_playing");
                return null;
            }
            return Malformed(state, "timestamp_invalid");
        }
        if (listenedAt < MinimumTimestamp || listenedAt > now.AddDays(1))
            return Malformed(state, "timestamp_invalid");

        var recordingMbid = Guid.TryParse(rawRecordingMbid, out var parsedMbid)
            ? parsedMbid.ToString("D")
            : null;
        var sourceItemKey = recordingMbid ?? Hash($"{title.ToUpperInvariant()}\u001f{artist.ToUpperInvariant()}\u001f{album?.ToUpperInvariant()}");
        return new(
            sequence,
            "lastfm",
            Hash("lastfm-export"),
            sourceItemKey,
            listenedAt,
            listenedAt,
            0,
            null,
            title,
            artist,
            album,
            null,
            recordingMbid,
            "Last.fm",
            null,
            null,
            false,
            null,
            false,
            ListeningHistoryImportClassification.Completed,
            "lastfm_scrobble");
    }

    private static bool TryItem(
        JsonElement element,
        string name,
        int maximum,
        out string? text,
        out string? mbid)
    {
        text = null;
        mbid = null;
        if (!element.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return true;
        return item.ValueKind == JsonValueKind.Object &&
               TryString(item, "#text", maximum, out text) &&
               TryString(item, "mbid", 36, out mbid);
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

    private static bool TryTimestamp(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        if (!element.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.Object) return false;
        if (date.TryGetProperty("uts", out var unix) && unix.ValueKind == JsonValueKind.String &&
            long.TryParse(unix.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
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
        return date.TryGetProperty("#text", out var text) && text.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParseExact(
                   text.GetString(),
                   "dd MMM yyyy, HH:mm",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out value);
    }

    private static bool IsNowPlaying(JsonElement element) =>
        element.TryGetProperty("@attr", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
        attributes.TryGetProperty("nowplaying", out var nowPlaying) && nowPlaying.ValueKind == JsonValueKind.String &&
        string.Equals(nowPlaying.GetString(), "true", StringComparison.OrdinalIgnoreCase);

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
        public bool Recognized { get; set; }
        public long Rows { get; set; }
        public long MusicRows { get; set; }
        public long Completed { get; set; }
        public long Skipped { get; set; }
        public long Malformed { get; set; }
        public long Duplicate { get; set; }
        public long RowsWithoutProviderIdentity { get; set; }
        public DateTimeOffset? Earliest { get; set; }
        public DateTimeOffset? Latest { get; set; }
        public HashSet<string> Occurrences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> LookupKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Reasons { get; } = new(StringComparer.Ordinal);
    }
}
