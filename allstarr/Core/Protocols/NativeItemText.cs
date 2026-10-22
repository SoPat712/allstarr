using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace allstarr.Core.Protocols;

internal static class NativeItemText
{
    public static string PreserveJson(string original, string updated, string idProperty)
        => PreserveJson([original], updated, idProperty);

    public static string PreserveJson(IEnumerable<string> originals, string updated, string idProperty)
    {
        using var after = JsonDocument.Parse(updated);
        var nativeItems = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        void Remember(JsonElement item)
        {
            if (IsNative(item, idProperty))
            {
                var key = JsonNode.Parse(item.GetRawText())!.ToJsonString();
                if (!nativeItems.TryGetValue(key, out var copies)) nativeItems[key] = copies = new();
                copies.Enqueue(item.GetRawText());
            }
            foreach (var child in Children(item)) Remember(child);
        }
        foreach (var original in originals)
        {
            using var before = JsonDocument.Parse(original);
            Remember(before.RootElement);
        }
        if (nativeItems.Count == 0) return updated;
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            void Write(JsonElement item)
            {
                if (IsNative(item, idProperty) && nativeItems.TryGetValue(JsonNode.Parse(item.GetRawText())!.ToJsonString(), out var copies) &&
                    copies.TryDequeue(out var raw))
                {
                    writer.WriteRawValue(raw);
                    return;
                }
                if (item.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (var property in item.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        Write(property.Value);
                    }
                    writer.WriteEndObject();
                }
                else if (item.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var child in item.EnumerateArray()) Write(child);
                    writer.WriteEndArray();
                }
                else item.WriteTo(writer);
            }
            Write(after.RootElement);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static string PreserveXml(string original, string updated, string? elementName = null)
    {
        var originals = XmlItems(original, elementName).GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<string>(group.Select(item => original[item.Start..item.End])), StringComparer.Ordinal);
        var edits = new List<(int Start, int End, string Text)>();
        var end = 0;
        foreach (var item in XmlItems(updated, elementName).OrderBy(item => item.Start))
        {
            if (item.Start < end || !originals.TryGetValue(item.Key, out var copies) || !copies.TryDequeue(out var raw)) continue;
            edits.Add((item.Start, item.End, raw));
            end = item.End;
        }
        var result = new StringBuilder(updated);
        foreach (var edit in edits.AsEnumerable().Reverse())
            result.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
        return result.ToString();
    }

    private static bool IsNative(JsonElement item, string idProperty) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(idProperty, out var id) &&
        id.ValueKind == JsonValueKind.String;

    private static IEnumerable<JsonElement> Children(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.Object => item.EnumerateObject().Select(property => property.Value),
        JsonValueKind.Array => item.EnumerateArray(),
        _ => []
    };

    private static IEnumerable<(int Start, int End, string Key)> XmlItems(string text, string? elementName)
    {
        var lines = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\n') lines.Add(index + 1);
        int Start(IXmlLineInfo info) => text.LastIndexOf('<', lines[info.LineNumber - 1] + info.LinePosition - 1);
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var keys = document.Descendants().Where(item => item.Attribute("id") != null ||
                elementName != null && item.Name.LocalName == elementName)
            .ToDictionary(item => Start((IXmlLineInfo)item), item => item.ToString(SaveOptions.DisableFormatting));
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { XmlResolver = null });
        var stack = new Stack<int>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var start = Start((IXmlLineInfo)reader);
                if (!reader.IsEmptyElement) stack.Push(start);
                else if (keys.TryGetValue(start, out var key)) yield return (start, TagEnd(text, start), key);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                var start = stack.Pop();
                if (keys.TryGetValue(start, out var key)) yield return (start, TagEnd(text, Start((IXmlLineInfo)reader)), key);
            }
        }
    }

    private static int TagEnd(string text, int start)
    {
        var quote = '\0';
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (character is '\'' or '"') quote = character;
            else if (character == '>') return index + 1;
        }
        throw new XmlException("Unterminated element.");
    }
}
