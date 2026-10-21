using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    private IActionResult MergePlayQueueExtension(SubsonicProxyResponse? upstream, string format)
    {
        var sonic = _audioMuse?.IsAvailable == true;
        var content = upstream is { IsSuccessStatusCode: true } ? Encoding.UTF8.GetString(upstream.Body) : null;
        if (_playQueues == null)
            return sonic ? MergeSonicExtension(upstream, format)
                : _relayProtocolAdapter.CreateResult(upstream!, $"application/{format}");

        if (format == "json")
        {
            JsonObject? document = null;
            try
            {
                document = content == null ? null : JsonNode.Parse(content) as JsonObject;
                if (document?["subsonic-response"]?["status"]?.GetValue<string>() != "ok") document = null;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
            document ??= new JsonObject
            {
                ["subsonic-response"] = new JsonObject { ["status"] = "ok", ["version"] = "1.16.1", ["openSubsonic"] = true }
            };
            var root = document["subsonic-response"]!.AsObject();
            var extensions = root["openSubsonicExtensions"] as JsonArray ?? [];
            root["openSubsonicExtensions"] = extensions;
            if (sonic && !extensions.OfType<JsonObject>().Any(item =>
                    item["name"]?.GetValue<string>() == "sonicSimilarity"))
                extensions.Add(new JsonObject { ["name"] = "sonicSimilarity", ["versions"] = new JsonArray(1) });
            foreach (var duplicate in extensions.OfType<JsonObject>().Where(item =>
                         item["name"]?.GetValue<string>() == "indexBasedQueue").ToArray())
                extensions.Remove(duplicate);
            extensions.Add(new JsonObject { ["name"] = "indexBasedQueue", ["versions"] = new JsonArray(1) });
            return JsonContent(document);
        }

        XDocument? xml = null;
        try
        {
            xml = content == null ? null : XDocument.Parse(content);
            if (xml?.Root?.Attribute("status")?.Value != "ok") xml = null;
        }
        catch (XmlException) { }
        xml ??= new XDocument(new XElement(XNamespace.Get("http://subsonic.org/restapi") + "subsonic-response",
            new XAttribute("status", "ok"), new XAttribute("version", "1.16.1")));
        var xmlRoot = xml.Root!;
        if (sonic && !xmlRoot.Elements().Any(item => item.Name.LocalName == "openSubsonicExtensions" &&
                item.Attribute("name")?.Value == "sonicSimilarity"))
            xmlRoot.Add(SonicExtensionXml(xmlRoot.Name.Namespace));
        xmlRoot.Elements().Where(item => item.Name.LocalName == "openSubsonicExtensions" &&
            item.Attribute("name")?.Value == "indexBasedQueue").Remove();
        xmlRoot.Add(new XElement(xmlRoot.Name.Namespace + "openSubsonicExtensions",
            new XAttribute("name", "indexBasedQueue"), new XElement(xmlRoot.Name.Namespace + "versions", 1)));
        return XmlContent(xml);
    }
}
