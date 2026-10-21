using System.Xml;
using System.Xml.Linq;

namespace allstarr.Core.Protocols.Subsonic;

internal static class SubsonicQueueNativeXml
{
    // Retain native attribute spelling, whitespace and unknown children; only the song wrapper changes.
    public static IReadOnlyList<(string Id, string Xml)> Entries(string source, string elementName, int depth = 1)
    {
        var lines = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
            if (source[i] is '\r' or '\n') lines.Add(i + 1);
        }
        using var reader = XmlReader.Create(new StringReader(source), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var entries = new List<(string, string)>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth || reader.LocalName != elementName) continue;
            var start = Offset();
            var name = reader.Name;
            var replacement = reader.Prefix.Length == 0 ? "entry" : reader.Prefix + ":entry";
            var id = reader.GetAttribute("id") ?? "";
            var empty = reader.IsEmptyElement;
            var inherited = ((IXmlNamespaceResolver)reader).GetNamespacesInScope(XmlNamespaceScope.ExcludeXml)
                .Where(pair => pair.Key.Length > 0 && reader.GetAttribute("xmlns:" + pair.Key) == null)
                .Select(pair => " " + new XAttribute(XNamespace.Xmlns + pair.Key, pair.Value)).ToArray();
            var endStart = start;
            if (!empty)
            {
                while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)) { }
                endStart = Offset();
            }
            var fragment = source[start..(TagEnd(source, endStart) + 1)];
            if (!empty)
            {
                var close = endStart - start + 2;
                fragment = fragment[..close] + replacement + fragment[(close + name.Length)..];
            }
            fragment = "<" + replacement + fragment[(name.Length + 1)..];
            if (inherited.Length > 0)
            {
                var openingEnd = TagEnd(fragment, 0);
                var insert = fragment[openingEnd - 1] == '/' ? openingEnd - 1 : openingEnd;
                fragment = fragment.Insert(insert, string.Concat(inherited));
            }
            entries.Add((id, fragment));
        }
        return entries;

        int Offset()
        {
            var info = (IXmlLineInfo)reader;
            var position = lines[info.LineNumber - 1] + info.LinePosition - 1;
            return source.LastIndexOf('<', position);
        }
    }

    private static int TagEnd(string source, int start)
    {
        char quote = '\0';
        for (var i = start; i < source.Length; i++)
        {
            var value = source[i];
            if (quote != '\0')
            {
                if (value == quote) quote = '\0';
            }
            else if (value is '\'' or '"') quote = value;
            else if (value == '>') return i;
        }
        throw new XmlException("Incomplete native song element.");
    }
}
