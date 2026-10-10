using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Matching;

namespace allstarr.Tests;

public sealed class MatchCorpusTests
{
    private const string CorpusFile = "match-corpus.json";
    private const string WriteVariable = "ALLSTARR_WRITE_MATCH_CORPUS";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly (string Title, string Artist, string Album, long Duration)[] Songs =
    [
        ("Blinding Lights", "The Weeknd", "After Hours", 200_040),
        ("Bohemian Rhapsody", "Queen", "A Night at the Opera", 354_320),
        ("Halo", "Beyoncé", "I Am... Sasha Fierce", 261_640),
        ("Dreams", "Fleetwood Mac", "Rumours", 257_800),
        ("Levitating", "Dua Lipa", "Future Nostalgia", 203_064),
        ("Rolling in the Deep", "Adele", "21", 228_093),
        ("Get Lucky", "Daft Punk", "Random Access Memories", 369_627),
        ("Sanguine Paradise", "Lil Uzi Vert", "Sanguine Paradise", 254_000),
        ("Cheek to Cheek", "Ella Fitzgerald & Louis Armstrong", "Ella & Louis", 355_000),
        ("Gurenge", "LiSA", "LEO-NiNE", 238_000),
        ("Despacito", "Luis Fonsi", "VIDA", 229_000),
        ("Smells Like Teen Spirit", "Nirvana", "Nevermind", 301_000)
    ];

    [Fact]
    public void CurrentDecisionsMatchTheRecordedCorpus()
    {
        var actual = Evaluate();
        var writePath = Environment.GetEnvironmentVariable(WriteVariable);
        if (!string.IsNullOrWhiteSpace(writePath))
        {
            File.WriteAllText(writePath, JsonSerializer.Serialize(actual, JsonOptions) + "\n");
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Matching", CorpusFile);
        var expected = JsonSerializer.Deserialize<List<CorpusResult>>(File.ReadAllText(path), JsonOptions)!;
        Assert.Equal(expected.Select(item => item.Case), actual.Select(item => item.Case));
        var changed = expected.Zip(actual)
            .Where(pair => JsonSerializer.Serialize(pair.First) != JsonSerializer.Serialize(pair.Second))
            .Select(pair => $"{pair.First.Case}: expected {Describe(pair.First)}, actual {Describe(pair.Second)}")
            .ToArray();
        Assert.True(changed.Length == 0,
            $"{changed.Length} corpus decisions changed:\n" + string.Join('\n', changed.Take(25)));
    }

    [Fact]
    public void CorpusCoversEveryVariantForEverySong()
    {
        var cases = Evaluate();

        Assert.Equal(Songs.Length * Variants.Length, cases.Count);
        Assert.Equal(cases.Count, cases.Select(item => item.Case).Distinct(StringComparer.Ordinal).Count());
    }

    private static string Describe(CorpusResult result) =>
        $"{result.State}/{result.Selected ?? "none"}/{result.Confidence:0.0000}";

    private static List<CorpusResult> Evaluate()
    {
        var engine = new TrackMatchDecisionEngine();
        var scope = new TrackMatchScope(
            StableGuid("scope-user"),
            "backend",
            StableGuid("scope-account"),
            PolicyVersion: 1,
            SourceSnapshotVersion: 1,
            AccessibleLibraryIds: new HashSet<string>(StringComparer.Ordinal) { "music" });
        var results = new List<CorpusResult>();
        for (var songIndex = 0; songIndex < Songs.Length; songIndex++)
        {
            var song = Songs[songIndex];
            var decoy = Songs[(songIndex + 1) % Songs.Length];
            foreach (var variant in Variants)
            {
                var name = $"{Slug(song.Title)}/{variant.Name}";
                var source = new ExternalTrackMatchSnapshot(
                    name, "deezer", $"source-{songIndex}", song.Title, song.Artist, song.Album, song.Artist,
                    song.Duration, null, null, IsExplicit: false);
                var candidates = new List<(string Label, LocalTrackMatchCandidate Candidate)>
                {
                    ("decoy", Candidate(name, "decoy", decoy.Title, song.Artist, decoy.Album, decoy.Duration))
                };
                var built = variant.Build(source, song, (label, title, artist, album, duration) =>
                    Candidate(name, label, title, artist, album, duration));
                candidates.AddRange(built.Candidates);
                var decision = engine.Decide(
                    scope,
                    built.Source,
                    candidates.Select(item => item.Candidate).ToArray(),
                    providerPriority: ["deezer", "qobuz"]);
                var selected = candidates.FirstOrDefault(item =>
                    item.Candidate.LibraryTrackId == decision.SelectedLibraryTrackId).Label;
                var selectedScore = decision.Candidates.FirstOrDefault(item =>
                    item.LibraryTrackId == decision.SelectedLibraryTrackId);
                results.Add(new CorpusResult(
                    name,
                    decision.State.ToString(),
                    selected,
                    Math.Round(decision.Confidence, 4),
                    decision.Reasons.Order(StringComparer.Ordinal).ToArray(),
                    (selectedScore?.Reasons ?? []).Order(StringComparer.Ordinal).ToArray()));
            }
        }

        return results;
    }

    private delegate LocalTrackMatchCandidate CandidateFactory(
        string label, string title, string artist, string? album, long? duration);

    private sealed record Variant(
        string Name,
        Func<ExternalTrackMatchSnapshot, (string Title, string Artist, string Album, long Duration), CandidateFactory,
            (ExternalTrackMatchSnapshot Source, IReadOnlyList<(string Label, LocalTrackMatchCandidate Candidate)> Candidates)> Build);

    private static readonly Variant[] Variants =
    [
        new("exact-metadata", (source, song, make) =>
            (source, [("match", make("match", song.Title, song.Artist, song.Album, song.Duration))])),
        new("isrc-agrees-title-differs", (source, song, make) =>
            (source with { Isrc = "USAAA2600001" },
                [("match", make("match", $"{song.Title} (2011 Remaster)", song.Artist, song.Album, song.Duration) with { Isrc = "USAAA2600001" })])),
        new("isrc-conflicts", (source, song, make) =>
            (source with { Isrc = "USAAA2600001" },
                [("match", make("match", song.Title, song.Artist, song.Album, song.Duration) with { Isrc = "USAAA2600002" })])),
        new("mbid-agrees", (source, song, make) =>
            (source with { MusicBrainzRecordingId = "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae" },
                [("match", make("match", song.Title.ToUpperInvariant(), song.Artist, null, song.Duration + 2_000) with { MusicBrainzRecordingId = "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae" })])),
        new("remaster-tag", (source, song, make) =>
            (source, [("match", make("match", $"{song.Title} - Remastered 2011", song.Artist, song.Album, song.Duration))])),
        new("live-version", (source, song, make) =>
            (source, [("match", make("match", $"{song.Title} (Live)", song.Artist, $"{song.Album} (Live)", song.Duration + 15_000))])),
        new("radio-edit-source", (source, song, make) =>
            (source with { Title = $"{song.Title} (Radio Edit)", DurationMilliseconds = song.Duration - 40_000 },
                [("match", make("match", song.Title, song.Artist, song.Album, song.Duration))])),
        new("instrumental", (source, song, make) =>
            (source, [("match", make("match", $"{song.Title} (Instrumental)", song.Artist, song.Album, song.Duration))])),
        new("featured-artist", (source, song, make) =>
            (source with { Title = $"{song.Title} (feat. Guest)" },
                [("match", make("match", song.Title, $"{song.Artist}, Guest", song.Album, song.Duration))])),
        new("duration-drift-3s", (source, song, make) =>
            (source, [("match", make("match", song.Title, song.Artist, song.Album, song.Duration + 3_000))])),
        new("duration-drift-30s", (source, song, make) =>
            (source, [("match", make("match", song.Title, song.Artist, song.Album, song.Duration + 30_000))])),
        new("case-and-diacritics", (source, song, make) =>
            (source, [("match", make("match", song.Title.ToUpperInvariant(), Fold(song.Artist), song.Album.Replace("&", "and"), song.Duration))])),
        new("album-mismatch", (source, song, make) =>
            (source, [("match", make("match", song.Title, song.Artist, "Greatest Hits", song.Duration + 1_000))])),
        new("local-vs-provider", (source, song, make) =>
            (source,
            [
                ("local", make("local", song.Title, song.Artist, song.Album, song.Duration + 4_000)),
                ("provider", make("provider", song.Title, song.Artist, song.Album, song.Duration) with
                {
                    IsLocal = false,
                    CanonicalRecordingId = null,
                    ProviderTrackIds = new Dictionary<string, string> { ["deezer"] = "deezer-track" }
                })
            ])),
        new("duplicate-locals", (source, song, make) =>
            (source,
            [
                ("first", make("first", song.Title, song.Artist, song.Album, song.Duration)),
                ("second", make("second", song.Title, song.Artist, song.Album, song.Duration))
            ]))
    ];

    private static LocalTrackMatchCandidate Candidate(
        string caseName, string label, string title, string artist, string? album, long? duration) => new(
        StableGuid($"{caseName}/{label}"),
        StableGuid("scope-user"),
        "backend",
        "music",
        $"item-{label}",
        StableGuid($"{caseName}/{label}/recording"),
        title,
        artist,
        album,
        artist,
        duration,
        null,
        null,
        IsExplicit: false);

    private static Guid StableGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static string Slug(string value) =>
        string.Concat(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    private static string Fold(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormD)
            .Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) !=
                                System.Globalization.UnicodeCategory.NonSpacingMark));

    private sealed record CorpusResult(
        string Case,
        string State,
        string? Selected,
        double Confidence,
        string[] DecisionReasons,
        string[] SelectedReasons);
}
