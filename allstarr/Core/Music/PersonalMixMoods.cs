using allstarr.Core.AudioMuse;

namespace allstarr.Core.Music;

internal static class PersonalMixMoods
{
    internal const string RecipeVersion = "moods-v2";
    internal static readonly string[] Kinds = ["chill", "focus", "workout", "party", "sleep"];
    internal static MixReadiness Readiness(int count) => count >= 25 ? new(true, "Ready") :
        new(false, $"Add about {25 - count} more qualifying analyzed or tagged songs.");
    internal static string Reason(string kind, MixCandidate row, string? providerName = null) => row.Source == "audiomuse"
        ? kind switch { "workout" => "Energetic sound", "party" => "Party sound", _ => "Calm sound" } : row.ChartTags?.FirstOrDefault(tag => Wanted(kind).Contains(tag)) is { } chartTag ? $"Last.fm tags this {chartTag} · from {providerName ?? "your music service"}"
        : "Tagged " + (row.Tags.Keys.FirstOrDefault(tag => TagScore(kind, new Dictionary<string, double> { [tag] = 1 }) != null) ?? kind) + (row.ProviderFilled ? " · from " + (providerName ?? "your music service") : "");

    internal static IReadOnlyList<MixCandidate> Qualify(string kind, IReadOnlyList<MixCandidate> rows,
        IReadOnlyDictionary<string, AudioMuseScores> features)
    {
        var result = new List<MixCandidate>();
        foreach (var row in rows)
        {
            var measured = row.Song.IsLocal && features.TryGetValue(row.Song.Id, out _);
            var score = measured ? Score(kind, features[row.Song.Id]) :
                TagScore(kind, row.Tags);
            if (score != null) result.Add(row with { Source = measured ? "audiomuse" : row.Source });
        }
        return result;
    }

    internal static string[] Wanted(string kind) => kind switch
    {
        "chill" => ["chill", "mellow", "calm", "dreamy"],
        "focus" => ["ambient", "classical", "electronic", "post-rock"],
        "sleep" => ["calm", "ambient", "mellow", "dreamy"],
        "workout" => ["energetic", "intense", "angry", "metal", "hard rock", "techno"],
        _ => ["dance", "disco", "house", "funk", "happy", "uplifting"]
    };
    internal static string Rule(string kind) => "Analyzed library songs require " + (kind switch
    {
        "chill" => "relaxed ≥ 0.6, happy ≥ 0.5; energy 0.2–0.7 when known.",
        "focus" => "relaxed ≥ 0.6, aggressive ≤ 0.4.",
        "sleep" => "relaxed ≥ 0.65, sad ≥ 0.5; energy ≤ 0.35 when known.",
        "workout" => "aggressive and danceable ≥ 0.6; energy ≥ 0.6 and tempo 120–170 when known.",
        _ => "party and danceable ≥ 0.6, happy ≥ 0.5."
    }) + " Otherwise, one of these tags: " + string.Join(", ", Wanted(kind)) + ".";

    // Feature thresholds qualify; missing optional measurements stay unknown.
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

    internal static double? TagScore(string kind, IReadOnlyDictionary<string, double> tags)
    {
        var wanted = Wanted(kind);
        var score = wanted.Sum(tag => Math.Max(0, tags.GetValueOrDefault(tag)));
        return score > 0 ? score : null;
    }
}
