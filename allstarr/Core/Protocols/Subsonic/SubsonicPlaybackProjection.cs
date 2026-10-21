using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace allstarr.Core.Protocols.Subsonic;

internal static class SubsonicPlaybackProjection
{
    public static byte[] AppendJson(byte[] body, byte[] entry)
    {
        var reader = new Utf8JsonReader(body);
        if (!reader.Read() || !FindProperty(ref reader, "subsonic-response") || reader.TokenType != JsonTokenType.StartObject)
            return body;
        if (!FindProperty(ref reader, "nowPlaying"))
            return Insert(body, checked((int)reader.TokenStartIndex), 0,
                Encoding.UTF8.GetBytes(",\"nowPlaying\":{\"entry\":[" + Encoding.UTF8.GetString(entry) + "]}"));
        if (reader.TokenType == JsonTokenType.Null)
            return Insert(body, checked((int)reader.TokenStartIndex), checked((int)(reader.BytesConsumed - reader.TokenStartIndex)),
                Encoding.UTF8.GetBytes("{\"entry\":[" + Encoding.UTF8.GetString(entry) + "]}"));
        if (reader.TokenType != JsonTokenType.StartObject) return body;
        using var document = JsonDocument.Parse(body);
        var list = document.RootElement.GetProperty("subsonic-response").GetProperty("nowPlaying");
        if (!FindProperty(ref reader, "entry"))
            return Insert(body, checked((int)reader.TokenStartIndex), 0,
                Encoding.UTF8.GetBytes((list.EnumerateObject().Any() ? "," : "") + "\"entry\":[" + Encoding.UTF8.GetString(entry) + "]"));
        if (reader.TokenType == JsonTokenType.Null)
            return Insert(body, checked((int)reader.TokenStartIndex), checked((int)(reader.BytesConsumed - reader.TokenStartIndex)),
                Encoding.UTF8.GetBytes("[" + Encoding.UTF8.GetString(entry) + "]"));
        if (reader.TokenType != JsonTokenType.StartArray) return body;
        var count = list.GetProperty("entry").GetArrayLength();
        reader.Skip();
        return Insert(body, checked((int)reader.TokenStartIndex), 0,
            Encoding.UTF8.GetBytes((count == 0 ? "" : ",") + Encoding.UTF8.GetString(entry)));
    }

    private static bool FindProperty(ref Utf8JsonReader reader, string name)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) return false;
            var matches = reader.ValueTextEquals(name);
            if (!reader.Read()) return false;
            if (matches) return true;
            reader.Skip();
        }
        return false;
    }

    public static byte[] AppendXml(byte[] body, XElement entry)
    {
        var source = Encoding.UTF8.GetString(body);
        var markup = entry.ToString(SaveOptions.DisableFormatting);
        using var reader = XmlReader.Create(new StringReader(source), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0 && reader.IsEmptyElement)
                return ExpandEmpty(source, reader, new XElement(entry.Name.Namespace + "nowPlaying", entry).ToString(SaveOptions.DisableFormatting));
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == "nowPlaying")
            {
                if (reader.IsEmptyElement) return ExpandEmpty(source, reader, markup);
                while (reader.Read())
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1)
                        return Encoding.UTF8.GetBytes(source.Insert(TokenStart(source, reader), markup));
                return body;
            }
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0)
                return Encoding.UTF8.GetBytes(source.Insert(TokenStart(source, reader),
                    new XElement(entry.Name.Namespace + "nowPlaying", entry).ToString(SaveOptions.DisableFormatting)));
        }
        return body;
    }

    private static byte[] ExpandEmpty(string source, XmlReader reader, string markup)
    {
        var start = TokenStart(source, reader);
        var quote = '\0';
        for (var index = start; index < source.Length; index++)
        {
            var character = source[index];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (character is '\'' or '"') quote = character;
            else if (character == '>')
                return Encoding.UTF8.GetBytes(source.Remove(index - 1, 2).Insert(index - 1, ">" + markup + "</" + reader.Name + ">"));
        }
        return Encoding.UTF8.GetBytes(source);
    }

    private static int TokenStart(string source, XmlReader reader)
    {
        var location = (IXmlLineInfo)reader;
        var index = 0;
        for (var line = 1; line < location.LineNumber; line++) index = source.IndexOf('\n', index) + 1;
        return source.LastIndexOf('<', Math.Min(source.Length - 1, index + location.LinePosition - 1));
    }

    private static byte[] Insert(byte[] body, int offset, int length, byte[] addition)
    {
        var result = new byte[body.Length - length + addition.Length];
        body.AsSpan(0, offset).CopyTo(result);
        addition.CopyTo(result, offset);
        body.AsSpan(offset + length).CopyTo(result.AsSpan(offset + addition.Length));
        return result;
    }
}
