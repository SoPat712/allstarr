using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace allstarr.Services.Subsonic;

internal static class SubsonicLyricsDiscovery
{
    public static SubsonicProxyResponse Merge(SubsonicProxyResponse? upstream, string format, bool ping = false)
    {
        if (ping && upstream?.IsSuccessStatusCode != true) return upstream!;
        try
        {
            if (format == "json")
            {
                var document = upstream is { IsSuccessStatusCode: true, Body.Length: > 0 }
                    ? JsonNode.Parse(upstream.Body) as JsonObject : null;
                var root = document?["subsonic-response"] as JsonObject;
                if (ping && root?["status"]?.GetValue<string>() != "ok") return upstream!;
                if (root?["status"]?.GetValue<string>() != "ok")
                {
                    root = new JsonObject { ["status"] = "ok", ["version"] = "1.16.1" };
                    document = new JsonObject { ["subsonic-response"] = root };
                }
                root!["openSubsonic"] = true;
                if (string.IsNullOrWhiteSpace(root["serverVersion"]?.GetValue<string>()))
                {
                    root["type"] = "Allstarr";
                    root["serverVersion"] = AppVersion.Version;
                }
                if (!ping)
                {
                    var extensions = root["openSubsonicExtensions"] as JsonArray ?? [];
                    var lyrics = extensions.OfType<JsonObject>()
                        .Where(item => item["name"]?.GetValue<string>() == "songLyrics").ToArray();
                    if (lyrics.Length == 0)
                        extensions.Add(new JsonObject { ["name"] = "songLyrics", ["versions"] = new JsonArray(1) });
                    else
                    {
                        // Provider projection supports line timing; backend versions cannot promise enhanced cues.
                        lyrics[0]["versions"] = new JsonArray(1);
                        foreach (var duplicate in lyrics.Skip(1)) extensions.Remove(duplicate);
                    }
                    root["openSubsonicExtensions"] = extensions;
                }
                return Response(Encoding.UTF8.GetBytes(document!.ToJsonString()), format);
            }

            var xml = upstream is { IsSuccessStatusCode: true, Body.Length: > 0 }
                ? XDocument.Parse(Encoding.UTF8.GetString(upstream.Body)) : null;
            var element = xml?.Root;
            if (ping && (element?.Name.LocalName != "subsonic-response" || element.Attribute("status")?.Value != "ok"))
                return upstream!;
            if (element?.Name.LocalName != "subsonic-response" || element.Attribute("status")?.Value != "ok")
            {
                element = new XElement(XNamespace.Get("http://subsonic.org/restapi") + "subsonic-response",
                    new XAttribute("status", "ok"), new XAttribute("version", "1.16.1"));
                xml = new XDocument(element);
            }
            element.SetAttributeValue("openSubsonic", "true");
            if (string.IsNullOrWhiteSpace(element.Attribute("serverVersion")?.Value))
            {
                element.SetAttributeValue("type", "Allstarr");
                element.SetAttributeValue("serverVersion", AppVersion.Version);
            }
            if (!ping)
            {
                var lyrics = element.Elements(element.Name.Namespace + "openSubsonicExtensions")
                    .Where(item => item.Attribute("name")?.Value == "songLyrics").ToArray();
                var extension = lyrics.FirstOrDefault();
                if (extension == null)
                {
                    extension = new XElement(element.Name.Namespace + "openSubsonicExtensions", new XAttribute("name", "songLyrics"));
                    element.Add(extension);
                }
                extension.Elements(element.Name.Namespace + "versions").Remove();
                extension.Add(new XElement(element.Name.Namespace + "versions", 1));
                foreach (var duplicate in lyrics.Skip(1)) duplicate.Remove();
            }
            return Response(Encoding.UTF8.GetBytes(xml!.ToString()), format);
        }
        catch (Exception exception) when (exception is JsonException or XmlException or InvalidOperationException)
        {
            return ping ? upstream! : Merge(null, format);
        }
    }

    private static SubsonicProxyResponse Response(byte[] body, string format) =>
        new(body, $"application/{format}", HttpStatusCode.OK, new Dictionary<string, string[]>());
}
