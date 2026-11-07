using System.Text;
using System.Xml;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

internal static class LyricsTiming
{
    internal static bool IsTimedText(string? content) => content?.TrimStart().StartsWith('<') == true;

    internal static IReadOnlyList<ProviderLyricsTimedLine>? ReadLines(LyricsInfo lyrics)
    {
        if (lyrics.TimedLines is { Count: > 0 }) return lyrics.TimedLines;
        if (!IsTimedText(lyrics.SyncedLyrics)) return null;
        try { return TtmlLyricsParser.Parse(lyrics.SyncedLyrics!, lyrics.Source ?? "unknown").RequireValue().TimedLines ?? []; }
        catch (Exception error) when (error is XmlException or ArgumentException or InvalidOperationException) { return []; }
    }

    internal static IReadOnlyList<ProviderLyricsTimedLine> VocalLines(IReadOnlyList<ProviderLyricsTimedLine> lines)
    {
        var output = new List<ProviderLyricsTimedLine>();
        foreach (var line in lines.SelectMany(line => line.VocalParts ?? [line]))
        {
            if (line.IsBackground == true || line.Segments?.Any(segment => segment.IsBackground == true) != true)
            {
                output.Add(line);
                continue;
            }
            var lead = line.Segments.Where(segment => segment.IsBackground != true).ToArray();
            if (lead.Length > 0)
                output.Add(new(string.Concat(lead.Select(segment => segment.Text)).Trim(),
                    line.Start, line.Duration, lead, voice: line.Voice, singer: line.Singer));
            var background = new List<ProviderLyricsTimedSegment>();
            foreach (var segment in line.Segments)
            {
                if (segment.IsBackground == true) background.Add(segment);
                else Flush();
            }
            Flush();

            void Flush()
            {
                if (background.Count == 0) return;
                var start = background.Min(segment => segment.Start);
                var end = background[^1].Duration.HasValue
                    ? background.Where(segment => segment.Duration.HasValue).Max(segment => segment.Start + segment.Duration!.Value)
                    : line.Duration is { } duration ? line.Start + duration : (TimeSpan?)null;
                output.Add(new(string.Concat(background.Select(segment => segment.Text)).Trim(), start,
                    end >= start ? end - start : null, background, true, line.Voice, line.Singer));
                background.Clear();
            }
        }
        return output.OrderBy(line => line.Start).ToArray();
    }

    internal static IReadOnlyList<TimeSpan?> FollowingStarts(IReadOnlyList<ProviderLyricsTimedLine> lines)
    {
        var starts = new TimeSpan?[lines.Count];
        var following = new Dictionary<(bool Background, string? Voice), TimeSpan>();
        foreach (var simultaneous in lines.Select((line, index) => (Line: line, Index: index)).GroupBy(item => item.Line.Start).Reverse())
        {
            foreach (var (line, index) in simultaneous)
                starts[index] = following.TryGetValue((line.IsBackground == true, line.Voice), out var next) ? next : null;
            foreach (var (line, _) in simultaneous)
                following[(line.IsBackground == true, line.Voice)] = line.Start;
        }
        return starts;
    }

    internal static IReadOnlyList<(int Position, int EndPosition, TimeSpan Start, TimeSpan? End)> Cues(
        ProviderLyricsTimedLine line, TimeSpan? fallbackEnd)
    {
        var cues = new List<(int Position, int EndPosition, TimeSpan Start, TimeSpan? End)>();
        var segments = line.Segments ?? [];
        var position = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var text = segment.Text;
            var start = line.Text.IndexOf(text, position, StringComparison.Ordinal);
            if (start < 0)
            {
                text = text.Trim();
                start = line.Text.IndexOf(text, position, StringComparison.Ordinal);
            }
            if (start < 0 || string.IsNullOrWhiteSpace(text)) continue;
            position = start + text.Length;
            cues.Add((start, position, segment.Start, segment.Duration is { } duration ? segment.Start + duration :
                index + 1 < segments.Count ? segments[index + 1].Start :
                line.Duration is { } lineDuration ? line.Start + lineDuration : fallbackEnd));
        }
        return cues;
    }

    internal static string? SidecarContent(LyricsInfo lyrics, long? measuredDurationMs = null)
    {
        var timed = ReadLines(lyrics);
        if (timed == null) return lyrics.SyncedLyrics ?? lyrics.PlainLyrics;
        var lines = VocalLines(timed);
        var following = FollowingStarts(lines);
        var rendered = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var end = following[index] is { } next ? next :
                measuredDurationMs is { } measured ? TimeSpan.FromMilliseconds(measured) :
                lyrics.Duration > 0 ? TimeSpan.FromSeconds(lyrics.Duration) : (TimeSpan?)null;
            var cues = Cues(line, end);
            var text = new StringBuilder("[" + Time(line.Start) + "]");
            var position = 0;
            for (var cueIndex = 0; cueIndex < cues.Count; cueIndex++)
            {
                var cue = cues[cueIndex];
                text.Append(line.Text.AsSpan(position, cue.Position - position));
                text.Append('<').Append(Time(cue.Start)).Append('>');
                text.Append(line.Text.AsSpan(cue.Position, cue.EndPosition - cue.Position));
                if (cue.End is { } cueEnd && (cueIndex + 1 == cues.Count || cues[cueIndex + 1].Start != cueEnd))
                    text.Append('<').Append(Time(cueEnd)).Append('>');
                position = cue.EndPosition;
            }
            text.Append(line.Text.AsSpan(position));
            rendered.Add(text.ToString().Replace('\r', ' ').Replace('\n', ' '));
        }
        return string.Join('\n', rendered);
    }

    private static string Time(TimeSpan value)
    {
        var ms = value.Ticks / TimeSpan.TicksPerMillisecond;
        return FormattableString.Invariant($"{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }
}
