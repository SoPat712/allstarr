namespace allstarr.Core.Enrichment;

public sealed record SharedTagInput(string RawLabel, double? Weight = null, bool IsGenre = false);
public sealed record SharedTagBatch(string Source, IReadOnlyList<SharedTagInput> Tags, DateTimeOffset FetchedAt);
public sealed record SharedRecordingTags(IReadOnlyList<SharedTagBatch> Batches, string? MusicBrainzArtistId);
public sealed record SharedTagEvidence(string Source, string RawLabel, string Label, string? CanonicalTag,
    double? Weight, bool IsGenre, DateTimeOffset FetchedAt);
public sealed record SharedCanonicalTag(string Tag, double Score, IReadOnlyList<SharedTagEvidence> Evidence);
public sealed record SharedSongTags(Guid RecordingId, IReadOnlyList<SharedCanonicalTag> Tags);

public static class SharedTagNormalization
{
    private static readonly HashSet<string> Vocabulary = ("rock|pop|hip-hop|r&b|electronic|jazz|blues|country|folk|classical|" +
        "metal|punk|soul|funk|disco|reggae|ska|dub|ambient|house|techno|trance|drum and bass|dubstep|dance|" +
        "indie rock|alternative rock|hard rock|progressive rock|psychedelic rock|post-rock|grunge|shoegaze|" +
        "dream pop|synth-pop|new wave|post-punk|hardcore punk|heavy metal|death metal|black metal|doom metal|" +
        "folk rock|country rock|bluegrass|americana|singer-songwriter|gospel|swing|bebop|fusion|bossa nova|" +
        "latin|salsa|reggaeton|afrobeat|world|soundtrack|experimental|acoustic|" +
        "happy|sad|calm|energetic|dark|uplifting|melancholic|romantic|angry|dreamy|chill|epic|mellow|playful|intense")
        .Split('|').ToHashSet(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["hip hop"] = "hip-hop",
        ["hiphop"] = "hip-hop",
        ["rap"] = "hip-hop",
        ["rnb"] = "r&b",
        ["r and b"] = "r&b",
        ["rhythm and blues"] = "r&b",
        ["electronica"] = "electronic",
        ["electronica/dance"] = "electronic",
        ["alternative"] = "alternative rock",
        ["indie"] = "indie rock",
        ["synthpop"] = "synth-pop",
        ["dnb"] = "drum and bass",
        ["drum & bass"] = "drum and bass",
        ["hip-hop/rap"] = "hip-hop",
        ["relaxing"] = "calm",
        ["relaxed"] = "calm",
        ["upbeat"] = "energetic",
        ["chillout"] = "chill",
        ["melancholy"] = "melancholic"
    };
    private static readonly string[] Junk = ["seen live", "favorite", "favourite", "albums i own", "my "];

    public static string? Canonical(string raw, IReadOnlyList<string> artists)
    {
        var label = raw.Trim().ToLowerInvariant();
        if (IsJunk(label, artists)) return null;
        label = Synonyms.GetValueOrDefault(label, label);
        return Vocabulary.Contains(label) ? label : null;
    }

    private static bool IsJunk(string label, IReadOnlyList<string> artists) => label.Length is 0 or > 100 ||
        artists.Any(artist => label == artist.Trim().ToLowerInvariant()) ||
        Junk.Any(value => label.Contains(value, StringComparison.Ordinal)) ||
        label.All(value => char.IsDigit(value) || value is 's' or '-' or ' ');

    public static IReadOnlyList<SharedTagEvidence> Evidence(IEnumerable<SharedTagBatch> batches, IReadOnlyList<string> artists) =>
        batches.SelectMany(batch => batch.Tags.Take(100).Where(tag => !string.IsNullOrWhiteSpace(tag.RawLabel) &&
                !IsJunk(tag.RawLabel.Trim().ToLowerInvariant(), artists) && (tag.Weight == null || double.IsFinite(tag.Weight.Value)))
            .Select(tag => new SharedTagEvidence(batch.Source, tag.RawLabel, tag.RawLabel.Trim().ToLowerInvariant(),
                Canonical(tag.RawLabel, artists), tag.Weight, tag.IsGenre, batch.FetchedAt)))
            .DistinctBy(tag => (tag.Source, tag.Label, tag.IsGenre)).ToArray();

    public static double SourceWeight(string source, bool genre) => source switch
    {
        "audiomuse" => 1.2,
        "musicbrainz-recording" => genre ? 1 : .85,
        "musicbrainz-release-group" => genre ? .8 : .65,
        "musicbrainz-artist" => genre ? .6 : .4,
        "lastfm-track" => .6,
        "lastfm-artist" or "spotify-artist" => .3,
        _ when source.EndsWith("-artist", StringComparison.Ordinal) => .3,
        _ => .75
    };

    public static IReadOnlyList<SharedCanonicalTag> Combine(IEnumerable<SharedTagEvidence> evidence)
    {
        var rows = evidence.Where(item => item.CanonicalTag != null).GroupBy(item => (item.Source, item.Label, item.IsGenre))
            .Select(group => group.MaxBy(item => item.FetchedAt)!).ToArray();
        var maxima = rows.GroupBy(item => item.Source).ToDictionary(group => group.Key,
            group => Math.Max(1, group.Max(item => Math.Max(0, item.Weight ?? 1))));
        return rows.GroupBy(item => item.CanonicalTag!).Select(group => new SharedCanonicalTag(group.Key,
                group.GroupBy(item => item.Source).Sum(source => source.Max(item => SourceWeight(item.Source, item.IsGenre) *
                    (item.Weight == null ? 1 : Math.Max(0, item.Weight.Value) / maxima[item.Source]))),
                group.OrderBy(item => item.Source, StringComparer.Ordinal).ThenBy(item => item.Label, StringComparer.Ordinal).ToArray()))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Tag, StringComparer.Ordinal).ToArray();
    }
}
