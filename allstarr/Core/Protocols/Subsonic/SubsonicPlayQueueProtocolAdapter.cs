using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Protocols.Subsonic;

public sealed record SubsonicQueueSave(string[] Ids, int? CurrentIndex, long Position);

public static class SubsonicPlayQueueProtocolAdapter
{
    private static readonly XNamespace Namespace = "http://subsonic.org/restapi";

    public static SubsonicQueueSave Parse(SubsonicRequestParameters parameters, bool byIndex)
    {
        var ids = parameters.GetAllValues("id").ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("A valid song ID is required.");
        var positionText = parameters.GetValueOrDefault("position");
        long position = 0;
        if (positionText.Length > 0 &&
            (!long.TryParse(positionText, NumberStyles.None, CultureInfo.InvariantCulture, out position) || position < 0))
            throw new ArgumentException("Position must be a nonnegative number of milliseconds.");
        var current = parameters.GetValueOrDefault(byIndex ? "currentIndex" : "current");
        if (ids.Length == 0)
        {
            if (current.Length > 0) throw new ArgumentException("An empty queue cannot have a current entry.");
            return new(ids, null, 0);
        }

        int index;
        if (byIndex)
        {
            if (!int.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index >= ids.Length)
                throw new ArgumentException("Current index must identify an entry in the queue.");
        }
        else
        {
            // Legacy clients also restore the first occurrence of the current song ID.
            index = Array.IndexOf(ids, current);
            if (index < 0) throw new ArgumentException("Current song must be in the queue.");
        }
        return new(ids, index, position);
    }

    public static string? NativeSong(SubsonicProxyResponse response, string format)
    {
        if (!response.IsSuccessStatusCode) return null;
        if (format == "json")
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement.GetProperty("subsonic-response");
            return root.GetProperty("status").GetString() == "ok" && root.TryGetProperty("song", out var song)
                ? song.GetRawText() : null;
        }
        var xml = XDocument.Parse(Encoding.UTF8.GetString(response.Body), LoadOptions.PreserveWhitespace);
        var entry = xml.Root?.Elements().FirstOrDefault(item => item.Name.LocalName == "song");
        if (xml.Root?.Attribute("status")?.Value != "ok" || entry == null) return null;
        return SubsonicQueueNativeXml.Entries(Encoding.UTF8.GetString(response.Body), "song").First().Xml;
    }

    public static SubsonicSavedQueue? NativeQueue(SubsonicProxyResponse response, string format, string username)
    {
        if (!response.IsSuccessStatusCode) return null;
        SubsonicQueueEntry[] entries;
        string? current;
        string? changed;
        string? changedBy;
        long position;
        if (format == "json")
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement.GetProperty("subsonic-response");
            if (root.GetProperty("status").GetString() != "ok") return null;
            if (!root.TryGetProperty("playQueue", out var queue))
                return new([], null, 0, username, DateTimeOffset.UnixEpoch, "");
            entries = queue.TryGetProperty("entry", out var songs) && songs.ValueKind == JsonValueKind.Array
                ? songs.EnumerateArray().Select(song => new SubsonicQueueEntry(
                    song.GetProperty("id").GetString()!, null, song.GetRawText(), null)).ToArray() : [];
            current = queue.TryGetProperty("current", out var value) ? value.GetString() : null;
            position = queue.TryGetProperty("position", out value) ? value.GetInt64() : 0;
            changed = queue.TryGetProperty("changed", out value) ? value.GetString() : null;
            changedBy = queue.TryGetProperty("changedBy", out value) ? value.GetString() : null;
        }
        else
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(response.Body), LoadOptions.PreserveWhitespace).Root;
            if (root?.Attribute("status")?.Value != "ok") return null;
            var queue = root.Elements().FirstOrDefault(item => item.Name.LocalName == "playQueue");
            entries = SubsonicQueueNativeXml.Entries(Encoding.UTF8.GetString(response.Body), "entry", 2)
                .Select(song => new SubsonicQueueEntry(song.Id, null, null, song.Xml)).ToArray();
            current = queue?.Attribute("current")?.Value;
            position = (long?)queue?.Attribute("position") ?? 0;
            changed = queue?.Attribute("changed")?.Value;
            changedBy = queue?.Attribute("changedBy")?.Value;
        }
        var index = Array.FindIndex(entries, entry => entry.Id == current);
        return new(entries, entries.Length == 0 ? null : Math.Max(index, 0), position, username,
            DateTimeOffset.TryParse(changed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
                ? timestamp : DateTimeOffset.UnixEpoch, changedBy ?? "");
    }

    public static IActionResult CreateResponse(SubsonicSavedQueue queue, string format, bool byIndex,
        SubsonicResponseBuilder builder)
    {
        var name = byIndex ? "playQueueByIndex" : "playQueue";
        var changed = queue.Changed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (format == "json")
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("subsonic-response");
                writer.WriteString("status", "ok");
                writer.WriteString("version", "1.16.1");
                writer.WriteBoolean("openSubsonic", true);
                writer.WriteStartObject(name);
                if (queue.CurrentIndex is { } index)
                {
                    if (byIndex) writer.WriteNumber("currentIndex", index);
                    else writer.WriteString("current", queue.Entries[index].Id);
                }
                writer.WriteNumber("position", queue.Position);
                writer.WriteString("username", queue.Username);
                writer.WriteString("changed", changed);
                writer.WriteString("changedBy", queue.ChangedBy);
                writer.WriteStartArray("entry");
                foreach (var entry in queue.Entries)
                {
                    if (entry.Song != null) JsonSerializer.Serialize(writer, builder.ConvertSongToJson(entry.Song));
                    else writer.WriteRawValue(entry.NativeJson!);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return new ContentResult { Content = Encoding.UTF8.GetString(stream.ToArray()), ContentType = "application/json" };
        }

        var xml = new StringBuilder();
        using (var writer = XmlWriter.Create(xml, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("subsonic-response", Namespace.NamespaceName);
            writer.WriteAttributeString("status", "ok");
            writer.WriteAttributeString("version", "1.16.1");
            writer.WriteStartElement(name, Namespace.NamespaceName);
            writer.WriteAttributeString("position", queue.Position.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("username", queue.Username);
            writer.WriteAttributeString("changed", changed);
            writer.WriteAttributeString("changedBy", queue.ChangedBy);
            if (queue.CurrentIndex is { } currentIndex)
                writer.WriteAttributeString(byIndex ? "currentIndex" : "current",
                    byIndex ? currentIndex.ToString(CultureInfo.InvariantCulture) : queue.Entries[currentIndex].Id);
            foreach (var entry in queue.Entries)
            {
                if (entry.Song != null)
                {
                    var song = builder.ConvertSongToXml(entry.Song, Namespace);
                    song.Name = Namespace + "entry";
                    song.WriteTo(writer);
                }
                else writer.WriteRaw(entry.NativeXml!);
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        return new ContentResult { Content = xml.ToString(), ContentType = "application/xml" };
    }
}
