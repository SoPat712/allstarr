using allstarr.Core.AudioMuse;

namespace allstarr.Core.Music;

internal static class PersonalMixMoods
{
    internal const string RecipeVersion = "moods-v1";
    internal static readonly string[] Kinds = ["chill", "focus", "workout", "party", "sleep"];
    internal static MixReadiness Readiness(int count) => count >= 25 ? new(true, "Ready") :
        new(false, $"Add about {25 - count} more qualifying analyzed or tagged songs.");
    internal static string Reason(string kind, MixCandidate row) => row.Source == "audiomuse"
        ? kind switch { "workout" => "Energetic sound", "party" => "Party sound", _ => "Calm sound" } : "Tagged " + kind;

    internal static IReadOnlyList<MixCandidate> Qualify(string kind, IReadOnlyList<MixCandidate> rows,
        IReadOnlyDictionary<string, AudioMuseScores> features)
    {
        var lowCoverage = features.Count < Math.Min(150, rows.Count(row => row.Song.IsLocal)) * .5 || features.Count == 0;
        var result = new List<MixCandidate>();
        foreach (var row in rows)
        {
            var measured = row.Song.IsLocal && features.TryGetValue(row.Song.Id, out _);
            var score = measured ? Score(kind, features[row.Song.Id]) :
                row.Song.IsLocal || lowCoverage ? TagScore(kind, row.Tags) : null;
            if (score is { } value) result.Add(row with { Score = row.Score + value, Source = measured ? "audiomuse" : "tags" });
        }
        return result;
    }

    // Version one: feature thresholds qualify; optional measurements refine the ordering.
    internal static double? Score(string kind, AudioMuseScores s)
    {
        var tags = s.Tags ?? new Dictionary<string, double>();
        var instrumental = new[] { "instrumental", "ambient", "electronic" }.Sum(tag => tags.GetValueOrDefault(tag));
        return kind switch
        {
            "chill" when s.Relaxed >= .6 && s.Happy >= .5 && (s.Energy == null || s.Energy is >= .2 and <= .7) =>
                s.Relaxed + s.Happy - Math.Abs((s.Energy ?? .45) - .45),
            "focus" when s.Relaxed >= .6 && s.Aggressive <= .4 =>
                s.Relaxed - s.Aggressive + .2 * instrumental + .2 * (1 - (s.LyricDensity ?? .5)),
            "sleep" when s.Relaxed >= .65 && s.Sad >= .5 && (s.Energy == null || s.Energy <= .35) =>
                s.Relaxed + s.Sad - (s.Energy ?? .2),
            "workout" when s.Aggressive >= .6 && s.Danceable >= .6 && (s.Energy == null || s.Energy >= .6) &&
                (s.Tempo == null || s.Tempo is >= 120 and <= 170) => s.Aggressive + s.Danceable + (s.Energy ?? .7),
            "party" when s.Party >= .6 && s.Danceable >= .6 && s.Happy >= .5 => s.Party + s.Danceable + s.Happy,
            _ => null
        };
    }

    private static double? TagScore(string kind, IReadOnlyDictionary<string, double> tags)
    {
        string[] wanted = kind switch
        {
            "chill" => ["chill", "mellow", "calm", "dreamy"],
            "focus" => ["ambient", "classical", "electronic", "post-rock"],
            "sleep" => ["calm", "ambient", "mellow", "dreamy"],
            "workout" => ["energetic", "intense", "angry", "metal", "hard rock", "techno"],
            _ => ["dance", "disco", "house", "funk", "happy", "uplifting"]
        };
        var score = wanted.Sum(tag => Math.Max(0, tags.GetValueOrDefault(tag)));
        return score > 0 ? score : null;
    }
}
