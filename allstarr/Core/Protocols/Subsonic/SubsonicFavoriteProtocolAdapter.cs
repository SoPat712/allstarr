using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Protocols.Subsonic;

public static partial class SubsonicFavoriteProtocolAdapter
{
    public static SubsonicProxyResponse Merge(SubsonicProxyResponse native, IReadOnlyList<FavoriteViewItem> favorites,
        string container, string format, SubsonicResponseBuilder builder)
    {
        var eligible = favorites.Where(item => item.Resource.ResourceKind is
            ProviderResourceKind.Track or ProviderResourceKind.Album or ProviderResourceKind.Artist).ToArray();
        if (!native.IsSuccessStatusCode || native.Body.Length == 0 || eligible.Length == 0) return native;
        try
        {
            byte[] body;
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                if (JsonNode.Parse(native.Body) is not JsonObject document ||
                    document["subsonic-response"] is not JsonObject root || root["status"]?.GetValue<string>() != "ok" ||
                    root[container] is not JsonObject list) return native;
                foreach (var favorite in eligible)
                {
                    var name = favorite.Display.Kind;
                    if (list[name] is not JsonArray items)
                    {
                        if (list[name] != null) return native;
                        list[name] = items = new JsonArray();
                    }
                    if (items.Any(item => item?["id"] is JsonValue id && id.TryGetValue<string>(out var value) && value == favorite.ItemId))
                        continue;
                    items.Add(JsonSerializer.SerializeToNode(builder.ConvertFavoriteToJson(favorite)));
                }
                body = Encoding.UTF8.GetBytes(NativeItemText.PreserveJson(Encoding.UTF8.GetString(native.Body), document.ToJsonString(), "id"));
            }
            else
            {
                var document = XDocument.Parse(Encoding.UTF8.GetString(native.Body), LoadOptions.PreserveWhitespace);
                var root = document.Root;
                if (root?.Name.LocalName != "subsonic-response" || root.Attribute("status")?.Value != "ok" ||
                    root.Element(root.Name.Namespace + container) is not { } list) return native;
                foreach (var favorite in eligible)
                {
                    if (list.Elements().Any(item => item.Attribute("id")?.Value == favorite.ItemId)) continue;
                    list.Add(builder.ConvertFavoriteToXml(favorite, root.Name.Namespace));
                }
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
