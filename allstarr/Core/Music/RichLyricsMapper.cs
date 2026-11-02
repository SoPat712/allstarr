using allstarr.Core.Capabilities;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

public static class RichLyricsMapper
{
    public static RichLyrics? Map(LyricsInfo? lyrics)
    {
        if (lyrics == null) return null;
        var sourceId = lyrics.Source ?? "unknown";
        var source = new RichLyricsSource(sourceId, SourceName(sourceId));
        if (string.IsNullOrWhiteSpace(lyrics.SyncedLyrics))
        {
            if (string.IsNullOrWhiteSpace(lyrics.PlainLyrics)) return null;
            return new(source, "plain", lyrics.PlainLyrics.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Split('\n').Select(text => new RichLyricsLine(null, null, text, "lead", [])).ToArray());
        }

        var lines = new List<RichLyricsLine>();
        if (lyrics.TimedLines is { Count: > 0 })
        {
            var timed = lyrics.TimedLines.SelectMany(line => line.VocalParts ?? [line]).OrderBy(line => line.Start).ToArray();
            var following = new Dictionary<(bool Background, string? Voice), TimeSpan>();
            var ends = new Dictionary<ProviderLyricsTimedLine, TimeSpan?>();
            foreach (var simultaneous in timed.GroupBy(line => line.Start).Reverse())
            {
                foreach (var line in simultaneous)
                {
                    var next = following.TryGetValue((line.IsBackground == true, line.Voice), out var nextStart) ? nextStart : (TimeSpan?)null;
                    ends[line] = line.Duration is { } duration ? line.Start + duration : WordEnd(line) ?? next ?? SongEnd(lyrics);
                }
                foreach (var line in simultaneous) following[(line.IsBackground == true, line.Voice)] = line.Start;
            }
            foreach (var line in timed) AddLine(lines, line, ends[line]);
            lines = lines.OrderBy(line => line.StartMs).ToList();
        }
        else
        {
            var timed = MusicLyrics.ParseSyncedLines(lyrics.SyncedLyrics);
            for (var index = 0; index < timed.Count; index++)
            {
                var line = timed[index];
                var end = index + 1 < timed.Count ? timed[index + 1].StartMilliseconds : Milliseconds(SongEnd(lyrics));
                var words = (line.Cues ?? []).Select(cue => new RichLyricsWord(cue.StartMilliseconds,
                    cue.EndMilliseconds ?? end, line.Text[cue.Position..cue.EndPosition])).ToArray();
                lines.Add(new(line.StartMilliseconds, end, line.Text, "lead", words));
            }
        }
        return lines.Count == 0 ? null : new(source, lines.Any(line => line.Words.Count > 0) ? "word" : "line", lines);
    }

    private static void AddLine(List<RichLyricsLine> output, ProviderLyricsTimedLine line, TimeSpan? end)
    {
        var segments = line.Segments ?? [];
        if (line.IsBackground == true || !segments.Any(segment => segment.IsBackground == true))
        {
            output.Add(Project(line, line.Text, line.Start, end, segments, line.IsBackground == true));
            return;
        }
        var lead = segments.Where(segment => segment.IsBackground != true).ToArray();
        if (lead.Length > 0)
            output.Add(Project(line, string.Concat(lead.Select(segment => segment.Text)).Trim(), line.Start, end, lead, false));
        var background = new List<ProviderLyricsTimedSegment>();
        foreach (var segment in segments)
        {
            if (segment.IsBackground == true) background.Add(segment);
            else Flush();
        }
        Flush();

        void Flush()
        {
            if (background.Count == 0) return;
            var last = background[^1];
            var backgroundEnd = last.Duration.HasValue
                ? background.Where(segment => segment.Duration.HasValue).Max(segment => segment.Start + segment.Duration!.Value) : end;
            output.Add(Project(line, string.Concat(background.Select(segment => segment.Text)).Trim(),
                background.Min(segment => segment.Start), backgroundEnd, background, true));
            background.Clear();
        }
    }

    private static RichLyricsLine Project(ProviderLyricsTimedLine line, string text, TimeSpan start, TimeSpan? end,
        IReadOnlyList<ProviderLyricsTimedSegment> segments, bool background)
    {
        var words = segments.Select((segment, index) => new RichLyricsWord(Milliseconds(segment.Start)!.Value,
            Milliseconds(segment.Duration is { } duration ? segment.Start + duration :
                index + 1 < segments.Count ? segments[index + 1].Start : end), segment.Text)).ToArray();
        return new(Milliseconds(start), Milliseconds(end), text, background ? "background" : "lead", words, line.Voice, line.Singer);
    }

    private static TimeSpan? WordEnd(ProviderLyricsTimedLine line)
    {
        var words = (line.Segments ?? []).Where(segment => line.IsBackground == true || segment.IsBackground != true).ToArray();
        return words.Length > 0 && words.All(word => word.Duration.HasValue)
            ? words.Max(word => word.Start + word.Duration!.Value) : null;
    }

    private static TimeSpan? SongEnd(LyricsInfo lyrics) => lyrics.Duration > 0 ? TimeSpan.FromSeconds(lyrics.Duration) : null;
    private static long? Milliseconds(TimeSpan? time) => time is { } value
        ? (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero) : null;
    private static string SourceName(string id) => id switch
    {
        "lyricsplus" => "LyricsPlus",
        "musixmatch" => "Musixmatch",
        "spicylyrics" => "SpicyLyrics",
        "lrclib" => "LRCLIB",
        "lrcred" => "lrc.red",
        "spotify" => "Spotify",
        "apple" => "Apple Music",
        "unison" => "Unison",
        "paxsenix" => "Paxsenix",
        "backend" => "Library",
        "kept" => "Kept lyrics",
        _ => id
    };
}
