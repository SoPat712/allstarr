using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

/// <summary>Parses the small, provider-native lyric documents returned by a backend.</summary>
public static class BackendLyrics
{
    private const int MaximumBodyBytes = 2 * 1024 * 1024;
    private const long MaximumMilliseconds = 24L * 60 * 60 * 1000;
    private const long TicksPerMillisecond = TimeSpan.TicksPerMillisecond;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const long MaximumTicks = MaximumMilliseconds * TicksPerMillisecond;

    public static LyricsInfo? ParseJellyfin(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        var rawPayload = payload.GetRawText();
        if (rawPayload.Length > MaximumBodyBytes ||
            Encoding.UTF8.GetByteCount(rawPayload) > MaximumBodyBytes)
            return null;

        if (!TryGetObject(payload, "Metadata", out var metadata) ||
            !TryGetArray(payload, "Lyrics", out var lyricItems))
            return null;

        if (!TryGetOptionalString(metadata, "Artist", out var artist) ||
            !TryGetOptionalString(metadata, "Album", out var album) ||
            !TryGetOptionalString(metadata, "Title", out var title) ||
            !TryGetOptionalBool(metadata, "IsSynced", out var synced) ||
            !TryGetOptionalInt64(metadata, "Length", out var lengthTicks) ||
            !TryGetOptionalInt64(metadata, "Offset", out var offsetTicks))
            return null;

        if (!TryDuration(lengthTicks, out var duration) ||
            !TryOffsetTicks(offsetTicks, out var offset))
            return null;

        var lines = new List<LyricLine>();
        foreach (var item in lyricItems.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("Text", out var textValue) ||
                textValue.ValueKind != JsonValueKind.String ||
                !TryGetOptionalInt64(item, "Start", out var startTicks))
                return null;

            var text = textValue.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var start = 0L;
            if (startTicks.HasValue && !TryTimestampTicks(startTicks.Value, out start))
                return null;

            lines.Add(new LyricLine(startTicks.HasValue, start, text));
        }

        if (lines.Count == 0)
            return null;

        var useTimed = synced ?? lines.All(line => line.HasStart);
        if (useTimed && lines.Any(line => !line.HasStart))
            return null;

        return CreateLyrics(
            artist,
            album,
            title,
            duration,
            offset,
            useTimed,
            lines);
    }

    public static LyricsInfo? ParseSubsonic(ReadOnlyMemory<byte> body, bool json)
    {
        if (body.Length == 0 || body.Length > MaximumBodyBytes)
            return null;

        return json
            ? ParseSubsonicJson(body)
            : ParseSubsonicXml(body);
    }

    private static LyricsInfo? ParseSubsonicJson(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!TryGetObject(root, "subsonic-response", out var response) ||
                !TryGetString(response, "status", out var status) ||
                !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ||
                !TryGetObject(response, "lyricsList", out var lyricsList) ||
                !TryGetArray(lyricsList, "structuredLyrics", out var entries))
                return null;

            Candidate? plain = null;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!TryReadJsonCandidate(entry, out var candidate))
                    continue;

                if (candidate.Synced)
                    return CreateLyrics(candidate);

                plain ??= candidate;
            }

            return plain.HasValue ? CreateLyrics(plain.Value) : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static LyricsInfo? ParseSubsonicXml(ReadOnlyMemory<byte> body)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumBodyBytes,
                MaxCharactersFromEntities = 0
            };

            using var stream = new MemoryStream(body.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root == null ||
                !HasName(root, "subsonic-response") ||
                !string.Equals(AttributeValue(root, "status"), "ok", StringComparison.OrdinalIgnoreCase))
                return null;

            var lyricsList = root.Elements().FirstOrDefault(e => HasName(e, "lyricsList"));
            if (lyricsList == null)
                return null;

            Candidate? plain = null;
            foreach (var entry in lyricsList.Elements().Where(e => HasName(e, "structuredLyrics")))
            {
                if (!TryReadXmlCandidate(entry, out var candidate))
                    continue;

                if (candidate.Synced)
                    return CreateLyrics(candidate);

                plain ??= candidate;
            }

            return plain.HasValue ? CreateLyrics(plain.Value) : null;
        }
        catch (XmlException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryReadJsonCandidate(JsonElement entry, out Candidate candidate)
    {
        candidate = default;
        if (entry.ValueKind != JsonValueKind.Object ||
            !TryGetRequiredBool(entry, "synced", out var synced) ||
            !TryGetOptionalString(entry, "displayArtist", out var artist) ||
            !TryGetOptionalString(entry, "displayTitle", out var title) ||
            !TryGetOptionalInt64(entry, "offset", out var offsetValue) ||
            !TryGetArray(entry, "line", out var lineItems))
            return false;

        var offset = offsetValue.GetValueOrDefault();
        if (!TryOffset(offset, out offset))
            return false;

        var lines = new List<LyricLine>();
        foreach (var item in lineItems.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetString(item, "value", out var text))
                return false;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var hasStart = item.TryGetProperty("start", out var startValue) &&
                           startValue.ValueKind != JsonValueKind.Null;
            var start = 0L;
            if (hasStart &&
                (startValue.ValueKind != JsonValueKind.Number ||
                 !startValue.TryGetInt64(out start) ||
                 !TryTimestamp(start, out start)))
                return false;

            if (synced && !hasStart)
                return false;

            lines.Add(new LyricLine(hasStart, start, text));
        }

        if (lines.Count == 0 || (synced && lines.Any(line => !line.HasStart)))
            return false;

        candidate = new Candidate(artist, title, offset, synced, lines);
        return true;
    }

    private static bool TryReadXmlCandidate(XElement entry, out Candidate candidate)
    {
        candidate = default;
        var syncedValue = AttributeValue(entry, "synced");
        if (syncedValue == null || !bool.TryParse(syncedValue, out var synced))
            return false;

        var artist = AttributeValue(entry, "displayArtist") ?? string.Empty;
        var title = AttributeValue(entry, "displayTitle") ?? string.Empty;
        var offsetValue = AttributeValue(entry, "offset");
        var offset = 0L;
        if (offsetValue != null &&
            (!long.TryParse(offsetValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) ||
             !TryOffset(offset, out offset)))
            return false;

        var lines = new List<LyricLine>();
        foreach (var item in entry.Elements().Where(e => HasName(e, "line")))
        {
            var text = item.Value;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var startValue = AttributeValue(item, "start");
            var hasStart = startValue != null;
            var start = 0L;
            if (hasStart &&
                (!long.TryParse(startValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                 !TryTimestamp(start, out start)))
                return false;

            if (synced && !hasStart)
                return false;

            lines.Add(new LyricLine(hasStart, start, text));
        }

        if (lines.Count == 0 || (synced && lines.Any(line => !line.HasStart)))
            return false;

        candidate = new Candidate(artist, title, offset, synced, lines);
        return true;
    }

    private static LyricsInfo CreateLyrics(Candidate candidate) =>
        CreateLyrics(
            candidate.Artist,
            string.Empty,
            candidate.Title,
            0,
            candidate.Offset,
            candidate.Synced,
            candidate.Lines);

    private static LyricsInfo CreateLyrics(
        string artist,
        string album,
        string title,
        int duration,
        long offset,
        bool synced,
        IReadOnlyList<LyricLine> lines)
    {
        var result = new LyricsInfo
        {
            ArtistName = artist,
            AlbumName = album,
            TrackName = title,
            Duration = duration,
            Source = "backend"
        };

        if (synced)
            result.SyncedLyrics = RenderTimed(lines, offset);
        else
            result.PlainLyrics = string.Join('\n', lines.Select(line => line.Text));

        return result;
    }

    private static string RenderTimed(IReadOnlyList<LyricLine> lines, long offset)
    {
        var rendered = new StringBuilder();
        if (offset != 0)
            rendered.Append("[offset:").Append(offset.ToString(CultureInfo.InvariantCulture)).Append("]\n");

        for (var i = 0; i < lines.Count; i++)
        {
            if (i != 0)
                rendered.Append('\n');

            var milliseconds = lines[i].StartMilliseconds;
            var totalSeconds = milliseconds / 1000;
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            var remainder = milliseconds % 1000;
            rendered.Append('[')
                .Append(minutes.ToString("00", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(seconds.ToString("00", CultureInfo.InvariantCulture))
                .Append('.')
                .Append(remainder.ToString("000", CultureInfo.InvariantCulture))
                .Append(']')
                .Append(lines[i].Text);
        }

        return rendered.ToString();
    }

    private static bool TryDuration(long? ticks, out int duration)
    {
        duration = 0;
        if (!ticks.HasValue)
            return true;
        if (ticks.Value < 0 || ticks.Value / TicksPerSecond > int.MaxValue)
            return false;

        duration = (int)(ticks.Value / TicksPerSecond);
        return true;
    }

    private static bool TryOffsetTicks(long? ticks, out long offset)
    {
        offset = 0;
        if (!ticks.HasValue)
            return true;
        if (ticks.Value < -MaximumTicks || ticks.Value > MaximumTicks)
            return false;
        return TryOffset(ticks.Value / TicksPerMillisecond, out offset);
    }

    private static bool TryTimestampTicks(long ticks, out long timestamp)
    {
        timestamp = 0;
        return ticks >= 0 &&
               ticks <= MaximumTicks &&
               TryTimestamp(ticks / TicksPerMillisecond, out timestamp);
    }

    private static bool TryTimestamp(long value, out long timestamp)
    {
        timestamp = value;
        return value >= 0 && value <= MaximumMilliseconds;
    }

    private static bool TryOffset(long value, out long offset)
    {
        offset = value;
        return value >= -MaximumMilliseconds && value <= MaximumMilliseconds;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetOptionalString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetRequiredBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property) ||
            (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetOptionalBool(JsonElement element, string name, out bool? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False)
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetOptionalInt64(JsonElement element, string name, out long? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number))
            return false;
        value = number;
        return true;
    }

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    private static bool HasName(XElement element, string name) =>
        element.Name.LocalName == name;

    private readonly record struct LyricLine(bool HasStart, long StartMilliseconds, string Text);

    private readonly record struct Candidate(
        string Artist,
        string Title,
        long Offset,
        bool Synced,
        IReadOnlyList<LyricLine> Lines);
}
