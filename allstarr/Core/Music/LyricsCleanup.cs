using System.Text;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

internal static partial class LyricsCleanup
{
    internal static LyricsInfo Clean(LyricsInfo lyrics)
    {
        var lines = CleanTimed(ReadTimed(lyrics));
        var synced = string.IsNullOrWhiteSpace(lyrics.SyncedLyrics) ? null : lines;
        if (lyrics.TimedLines is { Count: > 0 } && !LyricsTiming.IsTimedText(lyrics.SyncedLyrics) &&
            MusicLyrics.ParseSyncedLines(lyrics.SyncedLyrics).Count > 0)
            synced = CleanTimed(ReadTimed(new() { SyncedLyrics = lyrics.SyncedLyrics }));
        var plain = lyrics.PlainLyrics?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(text => CleanParts([text])[0]).Where(text => !Marker().IsMatch(text.Trim())).ToList();
        if (plain != null)
        {
            var first = plain.FindIndex(text => !string.IsNullOrWhiteSpace(text) && !IsBreak(text));
            var last = plain.FindLastIndex(text => !string.IsNullOrWhiteSpace(text) && !IsBreak(text));
            plain = plain.Where((text, index) => !IsBreak(text) || first >= 0 && index >= first && index <= last).ToList();
            for (var index = plain.Count - 1; index > 0; index--)
                if (IsBreak(plain[index]) && IsBreak(plain[index - 1])) plain.RemoveAt(index);
        }
        return new()
        {
            Id = lyrics.Id,
            TrackName = lyrics.TrackName,
            ArtistName = lyrics.ArtistName,
            AlbumName = lyrics.AlbumName,
            Duration = lyrics.Duration,
            Instrumental = lyrics.Instrumental,
            Source = lyrics.Source,
            Revision = lyrics.Revision,
            Metadata = lyrics.Metadata,
            Versions = lyrics.Versions,
            Attribution = lyrics.Attribution,
            CacheProvenance = lyrics.CacheProvenance,
            TimingCoverageMilliseconds = lyrics.TimingCoverageMilliseconds,
            PlainLyrics = plain == null ? null : string.Join('\n', plain.Select(text => IsBreak(text) ? "♪" : text)),
            SyncedLyrics = synced == null ? null : string.Join('\n', synced.Select(Render)),
            TimedLines = lines
        };
    }

    private static List<ProviderLyricsTimedLine>? CleanTimed(IReadOnlyList<ProviderLyricsTimedLine>? timed)
    {
        var lines = timed?.SelectMany(line => line.VocalParts ?? [line]).Select(CleanLine)
            .Where(line => !Marker().IsMatch(line.Text.Trim())).ToList();
        if (lines == null) return null;
        var first = lines.FindIndex(line => !string.IsNullOrWhiteSpace(line.Text) && !IsBreak(line.Text));
        var last = lines.FindLastIndex(line => !string.IsNullOrWhiteSpace(line.Text) && !IsBreak(line.Text));
        lines = lines.Where((line, index) => !IsBreak(line.Text) || first >= 0 && index >= first && index <= last).ToList();
        for (var index = lines.Count - 1; index > 0; index--)
            if (IsBreak(lines[index].Text) && IsBreak(lines[index - 1].Text)) lines.RemoveAt(index);
        return lines;
    }

    internal static bool NeedsCleanup(LyricsInfo lyrics)
    {
        var clean = Clean(lyrics);
        if (string.IsNullOrWhiteSpace(lyrics.SyncedLyrics)) return clean.PlainLyrics != lyrics.PlainLyrics;
        var original = ReadTimed(lyrics)?.SelectMany(line => line.VocalParts ?? [line]).ToArray() ?? [];
        var lines = clean.TimedLines ?? [];
        return original.Length != lines.Count || original.Where((line, index) => line.Text != lines[index].Text ||
            !(line.Segments ?? []).Select(segment => segment.Text).SequenceEqual((lines[index].Segments ?? []).Select(segment => segment.Text))).Any();
    }

    private static IReadOnlyList<ProviderLyricsTimedLine>? ReadTimed(LyricsInfo lyrics) => LyricsTiming.ReadLines(lyrics) ??
        (string.IsNullOrWhiteSpace(lyrics.SyncedLyrics) ? null : MusicLyrics.ParseSyncedLines(lyrics.SyncedLyrics).Select(line =>
            new ProviderLyricsTimedLine(line.Text, TimeSpan.FromMilliseconds(line.StartMilliseconds), segments: line.Cues?.Select(cue =>
                new ProviderLyricsTimedSegment(line.Text[cue.Position..cue.EndPosition], TimeSpan.FromMilliseconds(cue.StartMilliseconds),
                    cue.EndMilliseconds is { } end ? TimeSpan.FromMilliseconds(end - cue.StartMilliseconds) : null)).ToArray())).ToArray());

    private static ProviderLyricsTimedLine CleanLine(ProviderLyricsTimedLine line)
    {
        var text = CleanParts([line.Text])[0];
        var parts = CleanParts((line.Segments ?? []).Select(segment => segment.Text).ToArray(), line.Text);
        var segments = line.Segments?.Select((segment, index) => new ProviderLyricsTimedSegment(parts[index],
            segment.Start, segment.Duration, segment.IsBackground, segment.Synthetic)).ToArray();
        if (IsBreak(text))
        {
            text = "♪";
            segments = segments?.Select((segment, index) => new ProviderLyricsTimedSegment(index == 0 ? "♪" : "",
                segment.Start, segment.Duration, segment.IsBackground, segment.Synthetic)).ToArray();
        }
        return new(text, line.Start, line.Duration, segments, line.IsBackground, line.Voice, line.Singer);
    }

    private static string[] CleanParts(string[] parts, string? context = null)
    {
        var characters = new List<(char Value, int Part)>();
        var position = 0;
        for (var part = 0; part < parts.Length; part++)
        {
            var start = context?.IndexOf(parts[part], position, StringComparison.Ordinal) ?? -1;
            if (start >= position)
            {
                foreach (var value in context![position..start])
                    if (value is not (>= '\u200b' and <= '\u200d' or '\u2060' or '\ufeff'))
                        characters.Add((value is '’' or '‘' or '`' ? '\'' : value, -1));
                position = start + parts[part].Length;
            }
            foreach (var value in parts[part])
                if (value is not (>= '\u200b' and <= '\u200d' or '\u2060' or '\ufeff'))
                    characters.Add((value is '’' or '‘' or '`' ? '\'' : value, part));
        }
        var joined = string.Concat(characters.Select(item => item.Value));
        foreach (Match word in Word().Matches(joined))
        {
            if (!word.Value.Any(character => char.IsAsciiLetter(character) || character is >= '\u00c0' and <= '\u024f' or
                >= '\u1e00' and <= '\u1eff' or >= '\u2c60' and <= '\u2c7f' or >= '\ua720' and <= '\ua7ff' or >= '\uab30' and <= '\uab6f')) continue;
            for (var index = word.Index; index < word.Index + word.Length; index++)
                characters[index] = (Latin(characters[index].Value), characters[index].Part);
        }
        var output = parts.Select(_ => new StringBuilder()).ToArray();
        var space = false;
        foreach (var (value, part) in characters)
        {
            if (char.IsWhiteSpace(value))
            {
                if (!space && part >= 0) output[part].Append(' ');
                space = true;
            }
            else { if (part >= 0) output[part].Append(value); space = false; }
        }
        return output.Select(builder => builder.ToString()).ToArray();
    }

    internal static bool IsInstrumental(string text) => !text.Any(char.IsLetterOrDigit) || IsBreak(text) || Marker().IsMatch(text.Trim()) ||
        Instrumental().IsMatch(text);

    private static bool IsBreak(string text) => text.Any(character => character is '♪' or '♫' or '…' or '.') &&
        text.All(character => char.IsWhiteSpace(character) || character is '♪' or '♫' or '…' or '.');

    private static char Latin(char value) => value switch
    {
        'а' => 'a',
        'е' => 'e',
        'о' or 'ο' => 'o',
        'р' => 'p',
        'с' => 'c',
        'у' => 'y',
        'х' => 'x',
        'і' => 'i',
        'ј' => 'j',
        'ѕ' => 's',
        'А' or 'Α' => 'A',
        'В' or 'Β' => 'B',
        'Е' or 'Ε' => 'E',
        'К' or 'Κ' => 'K',
        'М' or 'Μ' => 'M',
        'Н' or 'Η' => 'H',
        'О' or 'Ο' => 'O',
        'Р' or 'Ρ' => 'P',
        'С' => 'C',
        'Т' or 'Τ' => 'T',
        'Х' or 'Χ' => 'X',
        'І' or 'Ι' => 'I',
        'Ј' => 'J',
        'Ѕ' => 'S',
        'Ν' => 'N',
        'Υ' => 'Y',
        'Ζ' => 'Z',
        _ => value
    };

    private static string Render(ProviderLyricsTimedLine line)
    {
        static string Time(TimeSpan value) => FormattableString.Invariant($"{(long)value.TotalMinutes:00}:{value.Seconds:00}.{value.Milliseconds:000}");
        var output = new StringBuilder("[" + Time(line.Start) + "]");
        var position = 0;
        foreach (var segment in line.Segments ?? [])
        {
            var text = segment.Text;
            var start = line.Text.IndexOf(text, position, StringComparison.Ordinal);
            if (start < 0) { text = text.Trim(); start = line.Text.IndexOf(text, position, StringComparison.Ordinal); }
            if (start < 0 || text.Length == 0) continue;
            output.Append(line.Text.AsSpan(position, start - position)).Append('<').Append(Time(segment.Start)).Append('>').Append(text);
            if (segment.Duration is { } duration) output.Append('<').Append(Time(segment.Start + duration)).Append('>');
            position = start + text.Length;
        }
        return output.Append(line.Text.AsSpan(position)).ToString();
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}']+")]
    private static partial Regex Word();
    [GeneratedRegex(@"^[\[(]\s*(?:intro|outro|verse|chorus|pre[- ]?chorus|post[- ]?chorus|hook|bridge|refrain|interlude|solo|break|instrumental)\s*\d*(?:\s*:\s*[^\])]+)?\s*[\])]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Marker();
    [GeneratedRegex(@"^[\[(]?\s*(?:instrumental|karaoke|backing track|no lyrics|music)\s*[\])\.!]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Instrumental();
}
