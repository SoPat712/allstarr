using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Intelligence;

// Apple's data export (privacy.apple.com, "Apple Media Services information"). Its play log has exact times but only
// song and album names, so each play takes its artist and Apple song id from the daily history (same UTC day and hour)
// or, failing that, from the library list in the same export.
public sealed class AppleMusicListeningHistoryImporter : IListeningHistoryImporter
{
    public const string ImporterRevision = "apple-music-play-activity-v1";
    public const string ProviderId = "apple-musickit";
    private const string PlayActivityName = "Apple Music Play Activity.csv";
    private const string DailyTracksName = "Apple Music - Play History Daily Tracks.csv";
    private const string LibraryTracksName = "Apple Music Library Tracks.json";
    private static readonly DateTimeOffset MinimumTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long MaximumMillisecondsPlayed = 24L * 60 * 60 * 1000;
    private const int MaximumArchiveEntries = 4096;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const long MaximumNestedArchiveBytes = 512L * 1024 * 1024;
    private const int MaximumFieldLength = 4000;

    public string Format => "apple-music-play-activity";
    public string Revision => ImporterRevision;

    public async Task<ListeningHistoryImportScan?> ScanAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow = null,
        CancellationToken cancellationToken = default)
    {
        if (context.MaximumRows is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(context), "MaximumRows must be between 1 and 10000000.");
        if (!source.CanSeek || !await IsZipAsync(source, cancellationToken)) return null;
        var budget = new ExpandedBudget();
        var files = new ExportFiles();
        try
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            await CollectAsync(archive, files, budget, depth: 0, cancellationToken);
            if (files.PlayActivity == null) return null;
            var state = new ScanState(Format);
            var lookup = new ArtistLookup(files.DailyTracks, files.LibraryTracks);
            var plays = ReadPlays(files.PlayActivity, context, state, cancellationToken);
            // Plays are numbered in the order they are emitted, oldest first, so a resumed import continues where it stopped.
            long sequence = 0;
            foreach (var play in Merge(plays))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = Parse(play with { Sequence = ++sequence }, lookup, context.Now, state);
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
            return new(
                state.Format, state.Rows, state.MusicRows, state.Completed, state.Partial, state.Skipped, 0, state.NonTrack,
                state.Malformed, state.Duplicate, state.RowsWithoutProviderIdentity, state.SourceUsers.Count,
                state.IdentityKeys.Count, state.Earliest, state.Latest, state.Reasons);
        }
        catch (JsonException exception)
        {
            throw new ListeningHistoryImportException(
                "history_import_json_invalid", "The Apple Music library list in the export is invalid.", exception);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
        {
            if (files.PlayActivity == null && exception is InvalidDataException) return null;
            throw new ListeningHistoryImportException(
                "history_import_archive_invalid", "The Apple Music export is invalid or unsupported.", exception);
        }
    }

    // Apple nests its zips: the download holds Apple_Media_Services.zip, which holds the play log and a zipped library list.
    private static async Task CollectAsync(ZipArchive archive, ExportFiles files, ExpandedBudget budget, int depth,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaximumArchiveEntries) throw ArchiveLimit();
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.Name;
            if (name.Equals(PlayActivityName, StringComparison.OrdinalIgnoreCase) && files.PlayActivity == null)
                files.PlayActivity = await ReadTextAsync(entry, budget, cancellationToken);
            else if (name.Equals(DailyTracksName, StringComparison.OrdinalIgnoreCase) && files.DailyTracks == null)
                files.DailyTracks = await ReadTextAsync(entry, budget, cancellationToken);
            else if (name.Equals(LibraryTracksName, StringComparison.OrdinalIgnoreCase) && files.LibraryTracks == null)
                files.LibraryTracks = await ReadTextAsync(entry, budget, cancellationToken);
            else if (depth < 2 && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                     (name.StartsWith("Apple_Media_Services", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals(LibraryTracksName + ".zip", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.Length is < 0 or > MaximumNestedArchiveBytes) throw ArchiveLimit();
                using var copy = new MemoryStream((int)entry.Length);
                await using (var stream = entry.Open()) await CopyBoundedAsync(stream, copy, budget, cancellationToken);
                copy.Position = 0;
                using var nested = new ZipArchive(copy, ZipArchiveMode.Read);
                await CollectAsync(nested, files, budget, depth + 1, cancellationToken);
            }
        }
    }

    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry, ExpandedBudget budget, CancellationToken cancellationToken)
    {
        if (entry.Length is < 0 or > MaximumNestedArchiveBytes) throw ArchiveLimit();
        using var copy = new MemoryStream((int)entry.Length);
        await using (var stream = entry.Open()) await CopyBoundedAsync(stream, copy, budget, cancellationToken);
        return new UTF8Encoding(false).GetString(copy.GetBuffer(), 0, (int)copy.Length).TrimStart('﻿');
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, ExpandedBudget budget, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            budget.Bytes += read;
            if (budget.Bytes > MaximumExpandedBytes) throw ArchiveLimit();
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static List<Play> ReadPlays(string csv, ListeningHistoryImportScanContext context, ScanState state,
        CancellationToken cancellationToken)
    {
        using var reader = new StringReader(csv);
        var header = ReadRecord(reader) ?? throw new ListeningHistoryImportException(
            "history_import_csv_invalid", "The Apple Music play log is empty.");
        var columns = header.Select((name, index) => (name: name.Trim(), index))
            .GroupBy(column => column.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        if (!columns.ContainsKey("Event Type") || !columns.ContainsKey("Song Name") || !columns.ContainsKey("Event Start Timestamp"))
            throw new ListeningHistoryImportException(
                "history_import_csv_invalid", "The Apple Music play log is missing its event, song or time columns.");
        string? Field(string[] record, string name) =>
            columns.TryGetValue(name, out var index) && index < record.Length && record[index].Trim() is { Length: > 0 } value ? value : null;
        var plays = new List<Play>();
        long lines = 0;
        while (ReadRecord(reader) is { } record)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The log also holds start and lyric events; a play is one PLAY_END row.
            if (++lines > context.MaximumRows * 4L)
                throw new ListeningHistoryImportException(
                    "history_import_row_limit", $"The history file exceeds the {context.MaximumRows} row limit.");
            if (Field(record, "Event Type") != "PLAY_END") continue;
            state.Rows++;
            if (state.Rows > context.MaximumRows)
                throw new ListeningHistoryImportException(
                    "history_import_row_limit", $"The history file exceeds the {context.MaximumRows} row limit.");
            if (record.Any(field => field.Length > MaximumFieldLength)) { Malformed(state, "malformed_field"); continue; }
            if (Field(record, "Media Type") is { } media && !media.Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                state.NonTrack++;
                AddReason(state.Reasons, "video");
                continue;
            }
            var song = Field(record, "Song Name");
            if (song == null)
            {
                state.NonTrack++;
                AddReason(state.Reasons, "non_track");
                continue;
            }
            if (!TryTimestamp(Field(record, "Event Start Timestamp"), out var started))
            {
                Malformed(state, "timestamp_invalid");
                continue;
            }
            var played = TryInt64(Field(record, "Play Duration Milliseconds"), out var value) ? value : -1;
            DateTimeOffset ended;
            if (TryTimestamp(Field(record, "Event End Timestamp"), out var end)) ended = end;
            else if (played >= 0) ended = started.AddMilliseconds(played);
            else { Malformed(state, "timestamp_invalid"); continue; }
            if (played < 0) played = Math.Max(0, (long)(ended - started).TotalMilliseconds);
            plays.Add(new(state.Rows, song, Field(record, "Album Name"), started, ended, played,
                TryInt64(Field(record, "Media Duration In Milliseconds"), out var duration) && duration > 0 ? duration : null,
                TryInt64(Field(record, "Start Position In Milliseconds"), out var startPosition) ? startPosition : null,
                TryInt64(Field(record, "End Position In Milliseconds"), out var endPosition) ? endPosition : null,
                Field(record, "End Reason Type"), Field(record, "Apple ID Number"),
                Field(record, "Device OS Name") ?? Field(record, "Client Platform") ?? Field(record, "Device Type")));
        }
        return plays;
    }

    // Pausing, suspending or scrubbing ends a log row; the next row of the same song picks up where it stopped.
    // Those pieces are one listen.
    private static IEnumerable<Play> Merge(List<Play> plays)
    {
        Play? current = null;
        foreach (var play in plays.OrderBy(play => play.Started).ThenBy(play => play.Sequence))
        {
            if (current != null && current.EndReason is "PLAYBACK_MANUALLY_PAUSED" or "PLAYBACK_SUSPENDED" or "SCRUB_BEGIN" or "SCRUB_END" &&
                play.Song == current.Song && play.Album == current.Album &&
                play.Started >= current.Ended.AddSeconds(-5) && play.Started <= current.Ended.AddHours(12) &&
                play.StartPosition is { } start && current.EndPosition is { } stop && Math.Abs(start - stop) <= 3000)
            {
                current = current with
                {
                    Ended = play.Ended,
                    Played = current.Played + play.Played,
                    EndPosition = play.EndPosition,
                    EndReason = play.EndReason
                };
                continue;
            }
            if (current != null) yield return current;
            current = play;
        }
        if (current != null) yield return current;
    }

    private static ListeningHistoryImportRow? Parse(Play play, ArtistLookup lookup, DateTimeOffset now, ScanState state)
    {
        if (play.Ended < MinimumTimestamp || play.Ended > now.AddDays(1)) return Malformed(state, "timestamp_invalid");
        if (play.Played is < 0 or > MaximumMillisecondsPlayed) return Malformed(state, "milliseconds_played_invalid");
        var found = lookup.Find(play);
        if (found == null) return Malformed(state, "missing_track_metadata");
        var (artist, appleId) = found.Value;

        ListeningHistoryImportClassification classification;
        string reasonCode;
        var halfway = play.Duration is { } duration && play.Played * 2 >= duration;
        if (play.Played >= 30_000 && (halfway || play.Played >= 240_000))
        {
            classification = ListeningHistoryImportClassification.Completed;
            reasonCode = play.EndReason == "NATURAL_END_OF_TRACK" ? "track_finished" : halfway ? "half_played" : "four_minute_cap";
        }
        else if (play.Played == 0)
        {
            classification = ListeningHistoryImportClassification.Skipped;
            reasonCode = "no_playback";
        }
        else if (play.EndReason is "TRACK_SKIPPED_FORWARDS" or "TRACK_SKIPPED_BACKWARDS")
        {
            classification = ListeningHistoryImportClassification.Skipped;
            reasonCode = "skip_button";
        }
        else
        {
            classification = ListeningHistoryImportClassification.Partial;
            reasonCode = "partial_play";
        }

        var sourceItemKey = appleId == null
            ? Hash($"{play.Song.ToUpperInvariant()}\u001f{artist.ToUpperInvariant()}\u001f{play.Album?.ToUpperInvariant()}")
            : Hash("apple:" + appleId);
        return new(
            play.Sequence,
            ProviderId,
            Hash(play.User ?? "unknown"),
            sourceItemKey,
            play.Ended.AddMilliseconds(-play.Played),
            play.Ended,
            play.Played,
            play.Duration,
            play.Song,
            artist,
            play.Album,
            appleId,
            null,
            play.Client,
            null,
            play.EndReason,
            false,
            null,
            false,
            classification,
            reasonCode);
    }

    private sealed class ArtistLookup
    {
        private readonly Dictionary<string, List<(HashSet<int> Hours, string Description, string? Id)>> _days = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _artistsByTitle = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Title, string Album), (string Artist, string? Id)> _library = new();
        private readonly Dictionary<string, HashSet<string>> _libraryArtistsByTitle = new(StringComparer.Ordinal);

        public ArtistLookup(string? dailyTracks, string? libraryTracks)
        {
            if (dailyTracks != null) ReadDaily(dailyTracks);
            if (libraryTracks != null) ReadLibrary(libraryTracks);
        }

        private void ReadDaily(string csv)
        {
            using var reader = new StringReader(csv);
            var header = ReadRecord(reader);
            if (header == null) return;
            var columns = header.Select((name, index) => (name: name.Trim(), index))
                .GroupBy(column => column.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
            if (!columns.TryGetValue("Date Played", out var dateColumn) || !columns.TryGetValue("Track Description", out var descriptionColumn)) return;
            var hoursColumn = columns.TryGetValue("Hours", out var hoursIndex) ? hoursIndex : -1;
            var idColumn = columns.TryGetValue("Track Identifier", out var id) ? id : -1;
            while (ReadRecord(reader) is { } record)
            {
                if (record.Length <= Math.Max(dateColumn, descriptionColumn)) continue;
                var description = record[descriptionColumn].Trim();
                if (description.Length == 0 || description.Length > MaximumFieldLength) continue;
                var hours = hoursColumn >= 0 && hoursColumn < record.Length
                    ? record[hoursColumn].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(hour => int.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1)
                        .Where(hour => hour is >= 0 and < 24).ToHashSet()
                    : [];
                var trackId = idColumn >= 0 && idColumn < record.Length ? AppleId(record[idColumn]) : null;
                var day = record[dateColumn].Trim();
                if (!_days.TryGetValue(day, out var rows)) _days[day] = rows = [];
                rows.Add((hours, description, trackId));
                // "Artist - Title": the title is whatever follows the last separator that leaves a known song.
                var separator = description.IndexOf(" - ", StringComparison.Ordinal);
                while (separator > 0)
                {
                    var title = description[(separator + 3)..];
                    if (!_artistsByTitle.TryGetValue(title, out var artists)) _artistsByTitle[title] = artists = new(StringComparer.Ordinal);
                    artists.Add(description[..separator]);
                    separator = description.IndexOf(" - ", separator + 3, StringComparison.Ordinal);
                }
            }
        }

        private void ReadLibrary(string json)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var track in document.RootElement.EnumerateArray())
            {
                if (track.ValueKind != JsonValueKind.Object) continue;
                var title = Text(track, "Title");
                var artist = Text(track, "Artist");
                if (title == null || artist == null) continue;
                var album = Text(track, "Album") ?? "";
                var id = track.TryGetProperty("Apple Music Track Identifier", out var value)
                    ? AppleId(value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    : null;
                _library.TryAdd((title.ToUpperInvariant(), album.ToUpperInvariant()), (artist, id));
                if (!_libraryArtistsByTitle.TryGetValue(title.ToUpperInvariant(), out var artists))
                    _libraryArtistsByTitle[title.ToUpperInvariant()] = artists = new(StringComparer.Ordinal);
                artists.Add(artist);
            }
        }

        public (string Artist, string? AppleId)? Find(Play play)
        {
            var suffix = " - " + play.Song;
            var reference = play.Ended;
            if (_days.TryGetValue(reference.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), out var rows))
            {
                var sameDay = rows.Where(row => row.Description.EndsWith(suffix, StringComparison.Ordinal) && row.Description.Length > suffix.Length).ToArray();
                var sameHour = sameDay.Where(row => row.Hours.Contains(reference.UtcDateTime.Hour)).ToArray();
                foreach (var candidates in new[] { sameHour, sameDay })
                {
                    if (candidates.Select(row => row.Description).Distinct(StringComparer.Ordinal).Count() != 1) continue;
                    var match = candidates.First();
                    return (match.Description[..^suffix.Length], candidates.Select(row => row.Id).FirstOrDefault(id => id != null));
                }
            }
            if (_library.TryGetValue((play.Song.ToUpperInvariant(), (play.Album ?? "").ToUpperInvariant()), out var library))
                return library;
            if (_artistsByTitle.TryGetValue(play.Song, out var artists) && artists.Count == 1)
                return (artists.First(), null);
            if (_libraryArtistsByTitle.TryGetValue(play.Song.ToUpperInvariant(), out var libraryArtists) && libraryArtists.Count == 1)
                return (libraryArtists.First(), null);
            return null;
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString()?.Trim() is { Length: > 0 and <= MaximumFieldLength } text && !text.Any(char.IsControl) ? text : null;
    }

    private static string? AppleId(string? value)
    {
        var id = value?.Trim();
        return id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit) && id != "0" ? id : null;
    }

    // RFC 4180 records: quoted fields may hold commas, doubled quotes and line breaks.
    private static string[]? ReadRecord(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                if (!any) return null;
                fields.Add(field.ToString());
                return fields.ToArray();
            }
            any = true;
            var character = (char)next;
            if (quoted)
            {
                if (character != '"') { field.Append(character); continue; }
                if (reader.Peek() == '"') { reader.Read(); field.Append('"'); continue; }
                quoted = false;
                continue;
            }
            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (reader.Peek() == '\n') reader.Read();
                    fields.Add(field.ToString());
                    return fields.ToArray();
                case '\n':
                    fields.Add(field.ToString());
                    return fields.ToArray();
                default:
                    field.Append(character);
                    break;
            }
        }
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

    private static bool TryInt64(string? value, out long number) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

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
        "The Apple Music export exceeds the entry count or 1 GiB expanded-size limit.");

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

    private sealed record Play(long Sequence, string Song, string? Album, DateTimeOffset Started, DateTimeOffset Ended,
        long Played, long? Duration, long? StartPosition, long? EndPosition, string? EndReason, string? User, string? Client);

    private sealed class ExportFiles
    {
        public string? PlayActivity { get; set; }
        public string? DailyTracks { get; set; }
        public string? LibraryTracks { get; set; }
    }

    private sealed class ExpandedBudget
    {
        public long Bytes { get; set; }
    }

    private sealed class ScanState(string format)
    {
        public string Format { get; } = format;
        public long Rows { get; set; }
        public long MusicRows { get; set; }
        public long Completed { get; set; }
        public long Partial { get; set; }
        public long Skipped { get; set; }
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
