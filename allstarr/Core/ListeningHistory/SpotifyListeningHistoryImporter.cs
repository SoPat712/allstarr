using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Intelligence;

public sealed class SpotifyListeningHistoryImporter : IListeningHistoryImporter
{
    public const string ImporterRevision = "spotify-extended-streaming-history-v2";
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private static readonly DateTimeOffset MinimumTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long MaximumMillisecondsPlayed = 24L * 60 * 60 * 1000;
    private const int MaximumArchiveEntries = 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;

    public string Format => "spotify-extended-streaming-history";
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
        var state = new ScanState(Format);
        try
        {
            await ScanRowsAsync(source, context, state, onRow, cancellationToken);
        }
        catch (JsonException exception)
        {
            if (!state.Recognized) return null;
            throw new ListeningHistoryImportException(
                "history_import_json_invalid",
                "The Spotify history file contains invalid JSON.",
                exception);
        }
        return Finish(state);
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
            if (archive.Entries.Count > MaximumArchiveEntries) throw ArchiveLimit();
            var entries = archive.Entries.Where(entry =>
                    entry.Name.StartsWith("Streaming_History_Audio_", StringComparison.OrdinalIgnoreCase) &&
                    entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();
            long declaredBytes = 0;
            foreach (var entry in entries)
            {
                if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - declaredBytes) throw ArchiveLimit();
                declaredBytes += entry.Length;
            }
            var state = new ScanState(Format);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = entry.Open();
                await using var bounded = new ArchiveEntryStream(stream, state);
                await ScanRowsAsync(bounded, context, state, onRow, cancellationToken);
            }
            return Finish(state);
        }
        catch (JsonException exception)
        {
            throw new ListeningHistoryImportException(
                "history_import_json_invalid", "The Spotify history file contains invalid JSON.", exception);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
        {
            throw new ListeningHistoryImportException(
                "history_import_archive_invalid", "The Spotify history archive is invalid or unsupported.", exception);
        }
    }

    private static async Task ScanRowsAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        ScanState state,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow,
        CancellationToken cancellationToken)
    {
        await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                           source, JsonOptions, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Rows++;
            if (state.Rows > context.MaximumRows)
                throw new ListeningHistoryImportException(
                    "history_import_row_limit",
                    $"The history file exceeds the {context.MaximumRows} row limit.");
            if (element.ValueKind != JsonValueKind.Object || !LooksLikeSpotify(element))
            {
                if (state.Recognized)
                {
                    state.Malformed++;
                    AddReason(state.Reasons, "schema_mismatch_row");
                }
                else
                {
                    state.UnrecognizedRows++;
                }
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
            var occurrence = Hash($"{row.SourceUserKey}\u001f{row.ListenedAt.ToUnixTimeMilliseconds()}\u001f{row.SourceItemKey}");
            if (!state.Occurrences.Add(occurrence))
            {
                state.Duplicate++;
                AddReason(state.Reasons, "duplicate");
                continue;
            }
            state.MusicRows++;
            if (row.ProviderTrackReference == null) state.RowsWithoutProviderIdentity++;
            state.SourceUsers.Add(row.SourceUserKey);
            state.IdentityKeys.Add(row.SourceItemKey);
            state.Earliest = state.Earliest == null || row.ListenedAt < state.Earliest ? row.ListenedAt : state.Earliest;
            state.Latest = state.Latest == null || row.ListenedAt > state.Latest ? row.ListenedAt : state.Latest;
            switch (row.Classification)
            {
                case ListeningHistoryImportClassification.Completed: state.Completed++; break;
                case ListeningHistoryImportClassification.Partial: state.Partial++; break;
                case ListeningHistoryImportClassification.Skipped: state.Skipped++; break;
            }
            AddReason(state.Reasons, row.ReasonCode);
            if (onRow != null) await onRow(row, cancellationToken);
        }
    }

    private static async Task<bool> IsZipAsync(Stream source, CancellationToken cancellationToken)
    {
        var position = source.Position;
        var header = new byte[4];
        var read = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        source.Position = position;
        return read == header.Length && header[0] == 'P' && header[1] == 'K' &&
               (header[2], header[3]) is ((byte)3, (byte)4) or ((byte)5, (byte)6) or ((byte)7, (byte)8);
    }

    private static ListeningHistoryImportException ArchiveLimit() => new(
        "history_import_archive_limit",
        "The Spotify history archive exceeds the entry count or 1 GiB expanded-size limit.");

    private static ListeningHistoryImportScan? Finish(ScanState state)
    {
        if (!state.Recognized) return null;
        return new(
            state.Format,
            state.Rows,
            state.MusicRows,
            state.Completed,
            state.Partial,
            state.Skipped,
            state.Episodes,
            state.NonTrack,
            state.Malformed,
            state.Duplicate,
            state.RowsWithoutProviderIdentity,
            state.SourceUsers.Count,
            state.IdentityKeys.Count,
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
        if (!TryString(element, "episode_name", 500, out var episode) ||
            !TryString(element, "episode_show_name", 500, out var episodeShow) ||
            !TryString(element, "spotify_episode_uri", 100, out var episodeUri))
            return Malformed(state, "malformed_episode");
        if (episode != null || episodeShow != null || episodeUri != null)
        {
            state.Episodes++;
            AddReason(state.Reasons, "episode");
            return null;
        }
        if (!TryString(element, "audiobook_title", 500, out var audiobook) ||
            !TryString(element, "audiobook_uri", 100, out var audiobookUri) ||
            !TryString(element, "audiobook_chapter_title", 500, out var chapter) ||
            !TryString(element, "audiobook_chapter_uri", 100, out var chapterUri))
            return Malformed(state, "malformed_audiobook");
        if (audiobook != null || audiobookUri != null || chapter != null || chapterUri != null)
        {
            state.NonTrack++;
            AddReason(state.Reasons, "audiobook");
            return null;
        }
        if (!TryString(element, "master_metadata_track_name", 500, out var title) ||
            !TryString(element, "master_metadata_album_artist_name", 500, out var artist) ||
            !TryString(element, "master_metadata_album_album_name", 500, out var album) ||
            !TryString(element, "username", 200, out var sourceUser) ||
            !TryString(element, "platform", 200, out var platform) ||
            !TryString(element, "reason_start", 100, out var reasonStart) ||
            !TryString(element, "reason_end", 100, out var reasonEnd) ||
            !TryString(element, "spotify_track_uri", 100, out var spotifyUri) ||
            !TryBoolean(element, "skipped", out var skipped) ||
            !TryBoolean(element, "offline", out var offline) ||
            !TryOfflineTimestamp(element, "offline_timestamp", now, out var offlineAt) ||
            !TryBoolean(element, "incognito_mode", out var privateSession))
            return Malformed(state, "malformed_field");
        if (title == null && artist == null && spotifyUri == null)
        {
            state.NonTrack++;
            AddReason(state.Reasons, "non_track");
            return null;
        }
        if (title == null || artist == null)
            return Malformed(state, "missing_track_metadata");
        if (!TryTimestamp(element, "ts", out var listenedAt) ||
            listenedAt < MinimumTimestamp || listenedAt > now.AddDays(1))
            return Malformed(state, "timestamp_invalid");
        if (!TryInt64(element, "ms_played", out var milliseconds) ||
            milliseconds is < 0 or > MaximumMillisecondsPlayed)
            return Malformed(state, "milliseconds_played_invalid");
        if (spotifyUri != null && !ValidSpotifyTrackUri(spotifyUri))
            return Malformed(state, "spotify_track_uri_invalid");

        var normalizedEnd = reasonEnd?.ToLowerInvariant();
        ListeningHistoryImportClassification classification;
        string reasonCode;
        if (skipped == true || normalizedEnd is "forwardbtn" or "backbtn")
        {
            classification = ListeningHistoryImportClassification.Skipped;
            reasonCode = skipped == true ? "spotify_skipped" : "skip_button";
        }
        else if (normalizedEnd == "trackdone" && milliseconds >= 30_000)
        {
            classification = ListeningHistoryImportClassification.Completed;
            reasonCode = "track_finished";
        }
        else if (milliseconds >= 240_000)
        {
            classification = ListeningHistoryImportClassification.Completed;
            reasonCode = "four_minute_cap";
        }
        else if (milliseconds == 0)
        {
            classification = ListeningHistoryImportClassification.Skipped;
            reasonCode = "no_playback";
        }
        else
        {
            classification = ListeningHistoryImportClassification.Partial;
            reasonCode = "partial_play";
        }

        var sourceUserKey = Hash(sourceUser ?? "unknown");
        var sourceItemKey = spotifyUri == null
            ? Hash($"{title.ToUpperInvariant()}\u001f{artist.ToUpperInvariant()}\u001f{album?.ToUpperInvariant()}")
            : Hash(spotifyUri);
        return new(
            sequence,
            "spotify",
            sourceUserKey,
            sourceItemKey,
            listenedAt.AddMilliseconds(-milliseconds),
            listenedAt,
            milliseconds,
            null,
            title,
            artist,
            album,
            spotifyUri,
            null,
            platform,
            reasonStart,
            reasonEnd,
            offline ?? false,
            offline == true ? offlineAt : null,
            privateSession ?? false,
            classification,
            reasonCode);
    }

    private static bool LooksLikeSpotify(JsonElement element) =>
        element.TryGetProperty("ts", out _) && element.TryGetProperty("ms_played", out _) &&
        (element.TryGetProperty("master_metadata_track_name", out _) ||
         element.TryGetProperty("episode_name", out _) ||
         element.TryGetProperty("episode_show_name", out _) ||
         element.TryGetProperty("spotify_track_uri", out _) ||
         element.TryGetProperty("spotify_episode_uri", out _) ||
         element.TryGetProperty("audiobook_title", out _) ||
         element.TryGetProperty("audiobook_uri", out _) ||
         element.TryGetProperty("audiobook_chapter_title", out _) ||
         element.TryGetProperty("audiobook_chapter_uri", out _));

    private static ListeningHistoryImportRow? Malformed(ScanState state, string reason)
    {
        state.Malformed++;
        AddReason(state.Reasons, reason);
        return null;
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

    private static bool TryBoolean(JsonElement element, string name, out bool? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }

    private static bool TryTimestamp(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParseExact(
                   property.GetString(),
                   "yyyy-MM-dd'T'HH:mm:ss'Z'",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out value);
    }

    private static bool TryOfflineTimestamp(
        JsonElement element,
        string name,
        DateTimeOffset now,
        out DateTimeOffset? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var milliseconds)) return false;
        if (milliseconds <= 0) return true;
        try
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if (timestamp < MinimumTimestamp || timestamp > now.AddDays(1)) return false;
            value = timestamp;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool ValidSpotifyTrackUri(string value)
    {
        const string prefix = "spotify:track:";
        return value.StartsWith(prefix, StringComparison.Ordinal) && value.Length == prefix.Length + 22 &&
               value[prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AddReason(IDictionary<string, long> reasons, string reason, long count = 1)
    {
        if (count == 0) return;
        reasons.TryGetValue(reason, out var current);
        reasons[reason] = current + count;
    }

    private sealed class ArchiveEntryStream(Stream source, ScanState state) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) =>
            TrackRead(source.Read(buffer[..AllowedReadLength(buffer.Length)]));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            TrackRead(await source.ReadAsync(buffer[..AllowedReadLength(buffer.Length)], cancellationToken));

        private int AllowedReadLength(int requested) =>
            (int)Math.Min(requested, MaximumExpandedBytes - state.ExpandedBytes + 1);

        private int TrackRead(int read)
        {
            state.ExpandedBytes += read;
            if (state.ExpandedBytes > MaximumExpandedBytes) throw ArchiveLimit();
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ScanState(string format)
    {
        public string Format { get; } = format;
        public bool Recognized { get; set; }
        public long Rows { get; set; }
        public long ExpandedBytes { get; set; }
        public long UnrecognizedRows { get; set; }
        public long MusicRows { get; set; }
        public long Completed { get; set; }
        public long Partial { get; set; }
        public long Skipped { get; set; }
        public long Episodes { get; set; }
        public long NonTrack { get; set; }
        public long Malformed { get; set; }
        public long Duplicate { get; set; }
        public long RowsWithoutProviderIdentity { get; set; }
        public DateTimeOffset? Earliest { get; set; }
        public DateTimeOffset? Latest { get; set; }
        public HashSet<string> Occurrences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SourceUsers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IdentityKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Reasons { get; } = new(StringComparer.Ordinal);
    }
}
