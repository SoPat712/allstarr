using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace allstarr.Services.Jellyfin;

internal static class JellyfinExternalPlaybackReport
{
    internal static string UpstreamId(string itemId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(itemId));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString();
    }

    internal static string? Text(JsonObject report, string field) =>
        report[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : null;

    internal static bool SameOccurrence(JsonObject previous, JsonObject current) =>
        new[] { "ItemId", "PlaySessionId", "SessionId", "PlaylistItemId" }.All(field =>
            Text(current, field) is not { } supplied || Text(previous, field) is not { } tracked || supplied == tracked);

    internal static string? SignalSession(JsonObject report)
    {
        var session = Text(report, "PlaySessionId") ?? Text(report, "SessionId");
        var occurrence = Text(report, "PlaylistItemId");
        return occurrence == null ? session : $"{session}:{occurrence}";
    }

    internal static string SerializeUpstream(JsonObject report)
    {
        var upstream = (JsonObject)report.DeepClone();
        if (Text(upstream, "ItemId") is { } id)
        {
            upstream["ItemId"] = UpstreamId(id);
            if (upstream["Item"] is JsonObject item)
            {
                item["Id"] = UpstreamId(id);
                if (Text(item, "AlbumId") is { } albumId && albumId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
                    item["AlbumId"] = UpstreamId(albumId);
            }
        }
        if (upstream["NowPlayingQueue"] is JsonArray queue)
            foreach (var entry in queue.OfType<JsonObject>())
                if (Text(entry, "Id") is { } entryId && entryId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
                    entry["Id"] = UpstreamId(entryId);
        return upstream.ToJsonString();
    }
}
