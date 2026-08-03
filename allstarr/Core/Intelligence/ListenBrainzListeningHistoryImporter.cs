using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Intelligence;

public sealed class ListenBrainzListeningHistoryImporter : IListeningHistoryImporter
{
    public const string ImporterRevision = "listenbrainz-export-v1";
    private static readonly DateTimeOffset MinimumTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaximumArchiveEntries = 1024;
    private const long MaximumEntryBytes = 128L * 1024 * 1024;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    private const int MaximumLineCharacters = 64 * 1024;
    private const long MaximumDurationMilliseconds = 24L * 60 * 60 * 1000;

    public string Format => "listenbrainz-export";
    public string Revision => ImporterRevision;

    public async Task<ListeningHistoryImportScan?> ScanAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow = null,
        CancellationToken cancellationToken = default)
    {
        if (context.MaximumRows is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(context), "MaximumRows must be between 1 and 10000000.");
        if (source.CanSeek && await IsZipAsync(source, cancellationToken))
            return await ScanArchiveAsync(source, context, onRow, cancellationToken);
        return await ScanJsonLinesAsync(source, context, onRow, cancellationToken);
    }

    private async Task<ListeningHistoryImportScan?> ScanArchiveAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var entries = ValidateArchive(archive);
            if (entries.Count == 0) return null;
            var state = new ScanState();
            foreach (var entry in entries.OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                await using var stream = entry.Open();
                await ScanLinesAsync(stream, context, state, onRow, cancellationToken);
            }
            return Finish(state);
        }
        catch (ListeningHistoryImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException or IOException or NotSupportedException)
        {
            throw new ListeningHistoryImportException(
                "history_import_archive_invalid",
                "The ListenBrainz export archive is invalid or unsafe.",
                exception);
        }
    }

    private async Task<ListeningHistoryImportScan?> ScanJsonLinesAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow,
        CancellationToken cancellationToken)
    {
        var state = new ScanState();
        try
        {
            await ScanLinesAsync(source, context, state, onRow, cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            if (!state.Recognized) return null;
            throw new ListeningHistoryImportException(
                "history_import_encoding_invalid",
                "The ListenBrainz history file is not valid UTF-8.",
                exception);
        }
        return Finish(state);
    }

    private async Task ScanLinesAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        ScanState state,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, StrictUtf8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            state.Rows++;
            if (state.Rows > context.MaximumRows)
                throw new ListeningHistoryImportException(
                    "history_import_row_limit",
                    $"The history file exceeds the {context.MaximumRows} row limit.");
            if (line.Length > MaximumLineCharacters)
                throw new ListeningHistoryImportException(
                    "history_import_line_limit",
                    $"A ListenBrainz row exceeds the {MaximumLineCharacters} character limit.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            }
            catch (JsonException)
            {
                if (state.Recognized) Malformed(state, "json_invalid");
                else state.UnrecognizedRows++;
                continue;
            }
            using (document)
            {
                var element = document.RootElement;
                if (!LooksLikeListenBrainz(element))
                {
                    if (state.Recognized) Malformed(state, "schema_mismatch_row");
                    else state.UnrecognizedRows++;
                    continue;
                }
                if (!state.Recognized)
                {
                    state.Recognized = true;
                    state.Malformed += state.UnrecognizedRows;
                    AddReason(state.Reasons, "schema_mismatch_row", state.UnrecognizedRows);
                    state.UnrecognizedRows = 0;
                }
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

    private static ListeningHistoryImportRow? Parse(
        JsonElement element,
        long sequence,
        DateTimeOffset now,
        ScanState state)
    {
        if (!element.TryGetProperty("track_metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !TryString(metadata, "track_name", 500, out var title) ||
            !TryString(metadata, "artist_name", 500, out var artist) ||
            !TryString(metadata, "release_name", 500, out var album) ||
            title == null || artist == null)
            return Malformed(state, "missing_track_metadata");
        if (!TryUnixTimestamp(element, "listened_at", out var listenedAt) ||
            listenedAt < MinimumTimestamp || listenedAt > now.AddDays(1))
            return Malformed(state, "timestamp_invalid");
        if (!TryString(element, "recording_msid", 36, out var recordingMsid))
            return Malformed(state, "recording_msid_invalid");

        string? recordingMbid = null;
        string? client = null;
        long? durationMilliseconds = null;
        if (metadata.TryGetProperty("additional_info", out var additional) && additional.ValueKind != JsonValueKind.Null)
        {
            if (additional.ValueKind != JsonValueKind.Object ||
                !TryString(additional, "recording_mbid", 36, out recordingMbid) ||
                !TryString(additional, "media_player", 200, out var mediaPlayer) ||
                !TryString(additional, "submission_client", 200, out var submissionClient) ||
                !TryDuration(additional, out durationMilliseconds))
                return Malformed(state, "additional_info_invalid");
            client = mediaPlayer ?? submissionClient;
        }
        if (recordingMbid == null && metadata.TryGetProperty("mbid_mapping", out var mapping) &&
            mapping.ValueKind != JsonValueKind.Null)
        {
            if (mapping.ValueKind != JsonValueKind.Object ||
                !TryString(mapping, "recording_mbid", 36, out recordingMbid))
                return Malformed(state, "mbid_mapping_invalid");
        }
        recordingMbid = Guid.TryParse(recordingMbid, out var parsedMbid) ? parsedMbid.ToString("D") : null;
        var sourceItemKey = recordingMbid ??
                            (Guid.TryParse(recordingMsid, out var parsedMsid)
                                ? Hash(parsedMsid.ToString("D"))
                                : Hash($"{title.ToUpperInvariant()}\u001f{artist.ToUpperInvariant()}\u001f{album?.ToUpperInvariant()}"));
        return new(
            sequence,
            "listenbrainz",
            Hash("listenbrainz-export"),
            sourceItemKey,
            listenedAt,
            listenedAt,
            0,
            durationMilliseconds,
            title,
            artist,
            album,
            null,
            recordingMbid,
            client ?? "ListenBrainz",
            null,
            null,
            false,
            null,
            false,
            ListeningHistoryImportClassification.Completed,
            "listenbrainz_scrobble");
    }

    private static bool LooksLikeListenBrainz(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("listened_at", out _) &&
        element.TryGetProperty("track_metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
        (metadata.TryGetProperty("track_name", out _) || metadata.TryGetProperty("artist_name", out _));

    private static bool TryDuration(JsonElement additional, out long? milliseconds)
    {
        milliseconds = null;
        if (additional.TryGetProperty("duration_ms", out var durationMs) && durationMs.ValueKind != JsonValueKind.Null)
        {
            if (durationMs.ValueKind != JsonValueKind.Number || !durationMs.TryGetInt64(out var value) ||
                value is < 0 or > MaximumDurationMilliseconds) return false;
            milliseconds = value;
            return true;
        }
        if (!additional.TryGetProperty("duration", out var duration) || duration.ValueKind == JsonValueKind.Null) return true;
        if (duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt64(out var seconds) ||
            seconds is < 0 or > MaximumDurationMilliseconds / 1000) return false;
        milliseconds = seconds * 1000;
        return true;
    }

    private static bool TryUnixTimestamp(JsonElement element, string name, out DateTimeOffset value)
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

    private static async Task<bool> IsZipAsync(Stream source, CancellationToken cancellationToken)
    {
        var position = source.Position;
        var header = new byte[4];
        var read = await source.ReadAsync(header, cancellationToken);
        source.Position = position;
        return read == header.Length && header[0] == 'P' && header[1] == 'K' &&
               (header[2], header[3]) is ((byte)3, (byte)4) or ((byte)5, (byte)6) or ((byte)7, (byte)8);
    }

    private static List<ZipArchiveEntry> ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is < 1 or > MaximumArchiveEntries)
            throw ArchiveInvalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var listens = new List<ZipArchiveEntry>();
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var segments = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entry.FullName.Length is < 1 or > 240 || entry.FullName.StartsWith('/') ||
                entry.FullName.Contains('\\') || segments.Contains("..", StringComparer.Ordinal) ||
                !names.Add(entry.FullName) || entry.Length < 0 || entry.CompressedLength < 0 ||
                unixFileType == 0xA000 || entry.Length > MaximumEntryBytes ||
                entry.Length > 1024 * 1024 + entry.CompressedLength * 100)
                throw ArchiveInvalid();
            expandedBytes += entry.Length;
            if (expandedBytes > MaximumExpandedBytes) throw ArchiveInvalid();
            if (entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                throw ArchiveInvalid();
            if (entry.FullName.StartsWith("listens/", StringComparison.Ordinal) &&
                entry.FullName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                listens.Add(entry);
        }
        return listens;
    }

    private static ListeningHistoryImportException ArchiveInvalid() => new(
        "history_import_archive_invalid",
        "The ListenBrainz export archive contains an unsafe or oversized entry.");

    private ListeningHistoryImportScan? Finish(ScanState state)
    {
        if (!state.Recognized) return null;
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
            state.RowsWithoutProviderIdentity,
            state.MusicRows > 0 ? 1 : 0,
            state.LookupKeys.Count,
            state.Earliest,
            state.Latest,
            state.Reasons);
    }

    private static ListeningHistoryImportRow? Malformed(ScanState state, string reason)
    {
        state.Malformed++;
        AddReason(state.Reasons, reason);
        return null;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AddReason(IDictionary<string, long> reasons, string reason, long count = 1)
    {
        if (count == 0) return;
        reasons.TryGetValue(reason, out var current);
        reasons[reason] = current + count;
    }

    private sealed class ScanState
    {
        public bool Recognized { get; set; }
        public long Rows { get; set; }
        public long UnrecognizedRows { get; set; }
        public long MusicRows { get; set; }
        public long Completed { get; set; }
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
