using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Favorites;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Protocols.Subsonic;

public static partial class SubsonicFavoriteProtocolAdapter
{
    public static SubsonicProxyResponse MergeArtists(SubsonicProxyResponse native,
        IReadOnlyList<FavoriteViewItem> favorites, string format, SubsonicResponseBuilder builder)
    {
        if (!native.IsSuccessStatusCode || native.Body.Length == 0 || favorites.Count == 0) return native;
        try
        {
            byte[] body;
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                if (JsonNode.Parse(native.Body) is not JsonObject document ||
                    document["subsonic-response"] is not JsonObject root || root["status"]?.GetValue<string>() != "ok" ||
                    root["artists"] is not JsonObject artists) return native;
                if (artists["index"] is not JsonArray indexes)
                {
                    if (artists["index"] != null) return native;
                    artists["index"] = indexes = new JsonArray();
                }
                var existing = indexes.OfType<JsonObject>().SelectMany(index =>
                    (index["artist"] as JsonArray ?? []).OfType<JsonObject>())
                    .Select(item => item["id"]?.ToString()).ToHashSet(StringComparer.Ordinal);
                foreach (var group in favorites.Where(item => !existing.Contains(item.ItemId))
                    .GroupBy(item => item.Display.Title[..1].ToUpperInvariant()).OrderBy(group => group.Key, StringComparer.Ordinal))
                    indexes.Add(new JsonObject
                    {
                        ["name"] = group.Key,
                        ["artist"] = new JsonArray(group.OrderBy(item => item.Display.Title, StringComparer.OrdinalIgnoreCase)
                            .Select(item => JsonSerializer.SerializeToNode(builder.ConvertFavoriteToJson(item))).ToArray())
                    });
                body = Encoding.UTF8.GetBytes(NativeItemText.PreserveJson(Encoding.UTF8.GetString(native.Body), document.ToJsonString(), "id"));
            }
            else
            {
                var document = XDocument.Parse(Encoding.UTF8.GetString(native.Body), LoadOptions.PreserveWhitespace);
                var root = document.Root;
                if (root?.Name.LocalName != "subsonic-response" || root.Attribute("status")?.Value != "ok" ||
                    root.Element(root.Name.Namespace + "artists") is not { } artists) return native;
                var ns = root.Name.Namespace;
                var existing = artists.Elements(ns + "index").Elements(ns + "artist")
                    .Select(item => item.Attribute("id")?.Value).ToHashSet(StringComparer.Ordinal);
                foreach (var group in favorites.Where(item => !existing.Contains(item.ItemId))
                    .GroupBy(item => item.Display.Title[..1].ToUpperInvariant()).OrderBy(group => group.Key, StringComparer.Ordinal))
                    artists.Add(new XElement(ns + "index", new XAttribute("name", group.Key),
                        group.OrderBy(item => item.Display.Title, StringComparer.OrdinalIgnoreCase)
                            .Select(item => builder.ConvertFavoriteToXml(item, ns))));
                body = Encoding.UTF8.GetBytes(NativeItemText.PreserveXml(Encoding.UTF8.GetString(native.Body), document.ToString(SaveOptions.DisableFormatting)));
            }
            return native with
            {
                Body = body,
                Headers = native.Headers.Where(header => !header.Key.Equals("ETag", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Expires", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Digest", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase)
            };
        }
        catch (Exception exception) when (exception is JsonException or System.Xml.XmlException or InvalidOperationException)
        { return native; }
    }
}
