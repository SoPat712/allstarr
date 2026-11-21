using System.Text;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

internal static partial class LyricsCleanup
{
    internal static LyricsInfo Clean(LyricsInfo lyrics)
    {
        var lines = WithoutCredits(CleanTimed(ReadTimed(lyrics)), line => line.Text, lyrics.TrackName);
        var synced = string.IsNullOrWhiteSpace(lyrics.SyncedLyrics) ? null : lines;
        if (lyrics.TimedLines is { Count: > 0 } && !LyricsTiming.IsTimedText(lyrics.SyncedLyrics) &&
            MusicLyrics.ParseSyncedLines(lyrics.SyncedLyrics).Count > 0)
            synced = WithoutCredits(CleanTimed(ReadTimed(new() { SyncedLyrics = lyrics.SyncedLyrics })), line => line.Text, lyrics.TrackName);
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
        var plainLines = WithoutCredits(plain, text => text, lyrics.TrackName);
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
            PlainLyrics = plainLines == null ? null : string.Join('\n', plainLines.Select(text => IsBreak(text) ? "♪" : text)),
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

    /// <summary>
    /// Some providers time the song's title and its credits ("Check - FLO", "Lyrics by：…", "作词：…") before the first
    /// sung line, and sometimes credits again after the last one. Only those leading and trailing blocks are removed,
    /// never a line between sung ones, and lyrics that are nothing but credits stay as they are.
    /// </summary>
    internal static string? WithoutCredits(string? text, string? title)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var remove = CreditLines(rows.Select(row => LrcTime().IsMatch(row) ? LrcTime().Replace(row, string.Empty) :
            LrcTag().IsMatch(row) ? null : row).ToArray(), title);
        return remove == null ? text : string.Join('\n', rows.Where((_, index) => !remove[index]));
    }

    internal static IReadOnlyList<T>? WithoutCredits<T>(IReadOnlyList<T>? items, Func<T, string?> text, string? title) =>
        items == null || CreditLines(items.Select(text).ToArray(), title) is not { } remove
            ? items : items.Where((_, index) => !remove[index]).ToArray();

    // Null entries (LRC tags) are kept and skipped over; blank and break lines go only with the block they sit in.
    private static bool[]? CreditLines(IReadOnlyList<string?> texts, string? title)
    {
        var lines = Enumerable.Range(0, texts.Count).Where(index => texts[index]?.Any(char.IsLetterOrDigit) == true).ToArray();
        if (lines.Length < 2) return null;
        var lead = 0;
        if (TitleLine().Match(texts[lines[0]]!.Trim()) is { Success: true } named &&
            (TitleKey(named.Groups[1].Value) is { Length: > 0 } key && key == TitleKey(title) || IsCredit(texts[lines[1]]!)))
            lead = 1;
        while (lead < lines.Length && IsCredit(texts[lines[lead]]!)) lead++;
        var tail = lines.Length;
        while (tail > lead && IsCredit(texts[lines[tail - 1]]!)) tail--;
        if (lead >= tail || lead == 0 && tail == lines.Length) return null;
        var remove = new bool[texts.Count];
        for (var index = 0; index < texts.Count; index++)
            remove[index] = texts[index] != null && (index < lines[lead] || index > lines[tail - 1]);
        return remove;
    }

    private static bool IsCredit(string text) => text.Length <= 1000 && Credit().IsMatch(text.Trim());

    private static string TitleKey(string? value) => new(Bracketed().Replace((value ?? "").Normalize(NormalizationForm.FormKC), "")
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
    [GeneratedRegex(@"^(?:词|曲|(?:作词|作曲|填词|谱曲|词曲|编曲|制作人|制作|监制|混音|母带|录音|吉他|贝斯|鼓手|键盘|钢琴|弦乐|和声|和音|配唱|人声|出品|发行|统筹|企划|策划|原唱|版权)\p{IsCJKUnifiedIdeographs}{0,5}|op|sp|(?:lyrics|words|music|written|co-written|composed|produced|co-produced|additional production|executive produced|arranged|mixed|mastered|recorded|engineered|programmed|vocals? produced|published) by|lyricists?|composers?|(?:song)?writers?|(?:co-|executive |vocal |additional )?producers?|arrangers?|arrangement|mix(?:ing)? engineers?|mixing|mastering(?: engineers?)?|recording engineers?|engineers?|programming|guitars?|bass|drums|keyboards?|(?:background|backing) vocals)(?:\s*[/&、,，]\s*(?:词|曲|作词|作曲|编曲|制作人|混音|母带|录音))*\s*[:：]\s*\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Credit();
    [GeneratedRegex(@"^(.+?)\s+[-–—]\s+\S", RegexOptions.CultureInvariant)]
    private static partial Regex TitleLine();
    [GeneratedRegex(@"[(\[（【][^)\]）】]*[)\]）】]", RegexOptions.CultureInvariant)]
    private static partial Regex Bracketed();
    [GeneratedRegex(@"\[\d{1,4}:\d{1,2}(?:[.:]\d{1,3})?\]|<\d{1,4}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex LrcTime();
    [GeneratedRegex(@"^\s*\[[A-Za-z][\w-]*:[^\]]*\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LrcTag();
    [GeneratedRegex(@"^[\[(]\s*(?:intro|outro|verse|chorus|pre[- ]?chorus|post[- ]?chorus|hook|bridge|refrain|interlude|solo|break|instrumental)\s*\d*(?:\s*:\s*[^\])]+)?\s*[\])]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Marker();
    [GeneratedRegex(@"^[\[(]?\s*(?:instrumental|karaoke|backing track|no lyrics|music)\s*[\])\.!]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Instrumental();
}
