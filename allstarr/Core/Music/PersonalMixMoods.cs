using allstarr.Core.AudioMuse;

namespace allstarr.Core.Music;

internal static class PersonalMixMoods
{
    internal const string RecipeVersion = "moods-v3";
    internal static readonly string[] Kinds = ["chill", "focus", "workout", "party", "sleep"];
    internal static MixReadiness Readiness(int count) => count >= 25 ? new(true, "Ready") :
        new(false, $"Add about {25 - count} more qualifying analyzed or tagged songs.");
    internal static string Reason(string kind, MixCandidate row, string? providerName = null) => row.Source == "audiomuse"
        ? kind switch { "workout" => "Energetic sound", "party" => "Party sound", _ => "Calm sound" } : row.ChartTags?.FirstOrDefault(tag => Wanted(kind).Contains(tag)) is { } chartTag ? $"Last.fm tags this {chartTag} · from {providerName ?? "your music service"}"
        : "Tagged " + (row.Tags.Keys.FirstOrDefault(tag => Wanted(kind).Contains(tag)) ?? row.Tags.Keys.FirstOrDefault(tag => Fitting(kind).Contains(tag)) ?? kind) + (row.ProviderFilled ? " · from " + (providerName ?? "your music service") : "");

    internal static IReadOnlyList<MixCandidate> Qualify(string kind, IReadOnlyList<MixCandidate> rows,
        IReadOnlyDictionary<string, AudioMuseScores> features)
    {
        var result = new List<(MixCandidate Row, double Score)>();
        foreach (var row in rows)
        {
            var measured = row.Song.IsLocal && features.TryGetValue(row.Song.Id, out _);
            // A style is only a hint, so it counts for songs the listener knows, not for picks from a service.
            var score = measured ? Score(kind, features[row.Song.Id]) :
                TagScore(kind, row.Tags, fitting: row.Song.IsLocal || row.Plays > 0);
            if (score != null) result.Add((row with { Source = measured ? "audiomuse" : row.Source }, score.Value));
        }
        // Songs that plainly carry the mood come before songs that only share a fitting style.
        return result.OrderByDescending(item => item.Score >= 1).Select(item => item.Row).ToList();
    }

    internal static string[] Wanted(string kind) => kind switch
    {
        "chill" => ["chill", "mellow", "calm", "dreamy"],
        "focus" => ["ambient", "classical", "electronic", "post-rock"],
        "sleep" => ["calm", "ambient", "mellow", "dreamy"],
        "workout" => ["energetic", "intense", "angry", "metal", "hard rock", "techno"],
        _ => ["dance", "disco", "house", "funk", "happy", "uplifting"]
    };

    // Styles that usually suit the mood. Few songs carry mood tags at all, so these fill in behind them at a lower score.
    internal static string[] Fitting(string kind) => kind switch
    {
        "chill" => ["acoustic", "dream pop", "soul", "singer-songwriter", "jazz", "folk", "r&b", "romantic"],
        "sleep" => ["classical", "acoustic", "dream pop", "melancholic", "sad", "singer-songwriter"],
        "workout" => ["hip-hop", "drum and bass", "dubstep", "trance", "punk", "heavy metal", "reggaeton", "house", "dance"],
        _ => []
    };
    internal static string Rule(string kind) => "Analyzed library songs require " + (kind switch
    {
        "chill" => "relaxed ≥ 0.6, happy ≥ 0.5; energy 0.2–0.7 when known.",
        "focus" => "relaxed ≥ 0.6, aggressive ≤ 0.4.",
        "sleep" => "relaxed ≥ 0.65, sad ≥ 0.5; energy ≤ 0.35 when known.",
        "workout" => "aggressive and danceable ≥ 0.6; energy ≥ 0.6 and tempo 120–170 when known.",
        _ => "party and danceable ≥ 0.6, happy ≥ 0.5."
    }) + " Otherwise, one of these tags: " + string.Join(", ", Wanted(kind)) + "." +
        (Fitting(kind) is { Length: > 0 } fitting ? " After those, songs in fitting styles: " + string.Join(", ", fitting) + "." : "");

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

    internal static double? TagScore(string kind, IReadOnlyDictionary<string, double> tags, bool fitting = false)
    {
        var wanted = Wanted(kind);
        var score = wanted.Sum(tag => Math.Max(0, tags.GetValueOrDefault(tag)));
        if (score > 0) return score;
        var close = fitting ? Fitting(kind).Sum(tag => Math.Max(0, tags.GetValueOrDefault(tag))) : 0;
        return close > 0 ? Math.Min(.5, close * .25) : null;
    }
}
