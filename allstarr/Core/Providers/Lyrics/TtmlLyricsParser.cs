using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Lyrics;

internal static partial class TtmlLyricsParser
{
    public static ProviderOutcome<ProviderLyricsResult> Parse(string xml, string source)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2_000_000
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName != "tt") throw new XmlException("Expected timed text.");
        var elements = document.Descendants().Take(LyricsTimingLimits.MaximumElements + 1).ToArray();
        if (elements.Length > LyricsTimingLimits.MaximumElements || elements.Any(element => element.Ancestors().Take(65).Count() > 64))
            throw new XmlException("Timed text exceeds supported bounds.");
        var performers = new Dictionary<string, (string Voice, string? Singer)>(StringComparer.Ordinal);
        foreach (var person in elements.Where(element => element.Name == Metadata + "agent"))
        {
            var id = (string?)person.Attribute(XNamespace.Xml + "id");
            if (string.IsNullOrWhiteSpace(id) || performers.ContainsKey(id)) continue;
            var name = person.Elements(Metadata + "name").FirstOrDefault()?.Value.Trim();
            performers[id] = ("v" + (performers.Count + 1).ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(name) ? null : name);
        }
        var lines = new List<ProviderLyricsTimedLine>();
        var plain = new List<string>();
        foreach (var paragraph in elements.Where(element => element.Name.LocalName == "p" &&
                     element.Ancestors().Any(parent => parent.Name.LocalName == "body")))
        {
            var text = new StringBuilder();
            var segments = new List<ProviderLyricsTimedSegment>();
            ReadNodes(paragraph, text, segments, false);
            var words = text.ToString().Trim();
            if (string.IsNullOrWhiteSpace(words)) continue;
            plain.Add(words);
            var start = ReadTime(paragraph, "begin") ?? (segments.Count > 0 ? segments.Min(segment => segment.Start) : (TimeSpan?)null);
            if (start == null) continue;
            var end = ReadTime(paragraph, "end");
            var duration = end.HasValue ? end - start : ReadTime(paragraph, "dur");
            var parts = new List<ProviderLyricsTimedLine>();
            var inheritedPerformer = paragraph.Ancestors().Select(parent => (string?)parent.Attribute(Metadata + "agent"))
                .FirstOrDefault(value => value != null);
            var inheritedBackground = paragraph.Ancestors().Any(parent => (string?)parent.Attribute(Metadata + "role") == "x-bg");
            ReadVocalParts(paragraph, inheritedBackground, inheritedPerformer, start.Value,
                duration.HasValue ? start + duration : null, performers, parts);
            lines.Add(new(words, start.Value, duration, segments.Count == 0 ? null : segments, vocalParts: parts));
        }
        if (plain.Count == 0)
            return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, source));
        var timed = lines.Count == plain.Count;
        var content = timed ? string.Join('\n', lines.Select(line =>
        {
            var ms = (long)line.Start.TotalMilliseconds;
            return FormattableString.Invariant($"[{ms / 60_000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}]{line.Text}");
        })) : string.Join('\n', plain);
        var revision = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Content = content, Lines = LyricsTimingRevision.Lines(lines) }))));
        return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available, source,
            timed ? ProviderLyricsFormat.LineTimed : ProviderLyricsFormat.PlainText, content, revision, timed ? lines : null));
    }

    private static readonly XNamespace Metadata = "http://www.w3.org/ns/ttml#metadata";

    private static void ReadVocalParts(XElement element, bool background, string? inheritedPerformer,
        TimeSpan fallbackStart, TimeSpan? fallbackEnd, Dictionary<string, (string Voice, string? Singer)> performers,
        List<ProviderLyricsTimedLine> parts)
    {
        var reference = (string?)element.Attribute(Metadata + "agent") ?? inheritedPerformer;
        background |= (string?)element.Attribute(Metadata + "role") == "x-bg";
        var boundaries = element.Descendants().Where(child =>
            (string?)child.Attribute(Metadata + "role") == "x-bg" ||
            child.Attribute(Metadata + "agent") is { } performer && performer.Value != reference).ToHashSet();
        var children = boundaries.Where(child => !child.Ancestors().TakeWhile(parent => parent != element).Any(boundaries.Contains)).ToArray();
        var text = new StringBuilder();
        var segments = new List<ProviderLyricsTimedSegment>();
        ReadNodes(Copy(element), text, segments, background);
        var start = ReadTime(element, "begin") ?? (segments.Count > 0 ? segments.Min(segment => segment.Start) : fallbackStart);
        var end = ReadTime(element, "end") ?? (ReadTime(element, "dur") is { } duration ? start + duration :
            segments.Count > 0 && segments.All(segment => segment.Duration.HasValue)
                ? segments.Max(segment => segment.Start + segment.Duration!.Value) : fallbackEnd);
        (string? Voice, string? Singer) identity = (null, null);
        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (!performers.TryGetValue(reference, out var known))
                performers[reference] = known = ("v" + (performers.Count + 1).ToString(CultureInfo.InvariantCulture), null);
            identity = known;
        }
        var words = text.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(words))
            parts.Add(new(words, start, end - start, segments, background ? true : null, identity.Voice, identity.Singer));
        foreach (var child in children)
            ReadVocalParts(child, background, reference, start, end, performers, parts);

        XElement Copy(XElement node) => new(node.Name, node.Attributes(), node.Nodes().Select(child =>
            child is XElement nested ? boundaries.Contains(nested) ? null : (object)Copy(nested) : child));
    }

    private static void ReadNodes(XElement element, StringBuilder text, List<ProviderLyricsTimedSegment> segments, bool background)
    {
        var role = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "role")?.Value;
        if (role is "x-translation" or "x-roman") return;
        background |= role == "x-bg";
        var hasTimedChildren = element.Descendants().Any(child => child.Attribute("begin") != null);
        var start = element.Name.LocalName == "span" ? ReadTime(element, "begin") : null;
        var initialLength = text.Length;
        foreach (var node in element.Nodes())
        {
            if (node is XText value)
            {
                var literal = Whitespace().Replace(value.Value, " ");
                text.Append(literal);
                if (string.IsNullOrWhiteSpace(literal) && value.PreviousNode is XElement && segments.Count > 0)
                {
                    var previous = segments[^1];
                    segments[^1] = new(previous.Text + literal, previous.Start, previous.Duration, previous.IsBackground, previous.Synthetic);
                }
            }
            else if (node is XElement child)
            {
                if (child.Name.LocalName == "br") text.Append(' ');
                else ReadNodes(child, text, segments, background);
            }
        }
        if (start.HasValue && !hasTimedChildren && text.Length > initialLength)
        {
            var end = ReadTime(element, "end");
            segments.Add(new(text.ToString(initialLength, text.Length - initialLength), start.Value,
                end.HasValue ? end - start : ReadTime(element, "dur"), background ? true : null));
        }
    }

    private static TimeSpan? ReadTime(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (value == null) return null;
        var components = value.Split(':');
        double seconds;
        if (components.Length is 2 or 3)
        {
            seconds = 0;
            foreach (var component in components)
            {
                if (!double.TryParse(component, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var part) || part < 0)
                    throw new XmlException("Invalid lyric timestamp.");
                seconds = seconds * 60 + part;
            }
        }
        else
        {
            var factor = value.EndsWith("ms", StringComparison.Ordinal) ? 0.001 : value.EndsWith('m') ? 60 : value.EndsWith('h') ? 3600 : 1;
            var number = value.EndsWith("ms", StringComparison.Ordinal) ? value[..^2] : value.EndsWith('s') || value.EndsWith('m') || value.EndsWith('h') ? value[..^1] : value;
            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds))
                throw new XmlException("Invalid lyric timestamp.");
            seconds *= factor;
        }
        if (!double.IsFinite(seconds) || seconds is < 0 or > 86400) throw new XmlException("Lyric timestamp exceeds supported bounds.");
        return TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
