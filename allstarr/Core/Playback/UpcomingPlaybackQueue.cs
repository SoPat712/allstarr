using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Text;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Playback;

public static class UpcomingPlaybackQueue
{
    public static IReadOnlyList<string>? ParseJellyfin(JsonElement report, string current)
    {
        if (!report.TryGetProperty("NowPlayingQueue", out var queue)) return null;
        if (queue.ValueKind != JsonValueKind.Array || queue.GetArrayLength() > 10000) return [];
        var occurrence = Text(report, "PlaylistItemId");
        var entries = queue.EnumerateArray().ToArray();
        var matches = entries.Select((entry, index) => (entry, index))
            .Where(pair => Text(pair.entry, "Id") == current &&
                (occurrence == null || Text(pair.entry, "PlaylistItemId") == occurrence)).ToArray();
        if (matches.Length != 1) return [];
        return entries.Skip(matches[0].index + 1).Take(2).Select(entry => Text(entry, "Id"))
            .Where(id => id is { Length: > 0 and <= 500 }).Select(id => id!).ToArray();
    }

    public static IReadOnlyList<string> After(IReadOnlyList<string> entries, string current)
    {
        var matches = entries.Select((id, index) => (id, index)).Where(pair => pair.id == current).ToArray();
        return matches.Length == 1 ? entries.Skip(matches[0].index + 1).Take(2).ToArray() : [];
    }

    public static bool IsSuccessfulSubsonicSave(SubsonicProxyResponse response)
    {
        if (!response.IsSuccessStatusCode) return false;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("subsonic-response", out var root) && root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                status.GetString() == "ok";
        }
        catch (JsonException)
        {
            try { return XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root?.Attribute("status")?.Value == "ok"; }
            catch (XmlException) { return false; }
        }
    }

    public static (string? Current, IReadOnlyList<string> Next) ParseSubsonic(SubsonicRequestParameters parameters, bool byIndex)
    {
        var entries = parameters.GetAllValues("id");
        var current = parameters.GetValueOrDefault("current");
        if (!byIndex) return (current, current == null ? [] : After(entries, current));
        if (!int.TryParse(current, out var index) || index < 0 || index >= entries.Count) return (null, []);
        return (entries[index], entries.Skip(index + 1).Take(2).ToArray());
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
