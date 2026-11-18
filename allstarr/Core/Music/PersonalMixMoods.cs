using allstarr.Core.AudioMuse;

namespace allstarr.Core.Music;

internal static class PersonalMixMoods
{
    internal const string RecipeVersion = "moods-v5";
    internal static readonly string[] Kinds = ["chill", "focus", "workout", "party", "sleep"];
    internal static MixReadiness Readiness(int count) => count >= 25 ? new(true, "Ready") :
        new(false, $"Add about {25 - count} more qualifying analyzed or tagged songs.");
    internal static string Reason(string kind, MixCandidate row, string? providerName = null) => row.Source == "audiomuse"
        ? kind switch { "workout" => "Energetic sound", "party" => "Party sound", _ => "Calm sound" } : row.ChartTags?.FirstOrDefault(tag => Wanted(kind).Contains(tag)) is { } chartTag ? $"Last.fm tags this {chartTag} · from {providerName ?? "your music service"}"
        : "Tagged " + (row.Tags.Keys.FirstOrDefault(tag => Wanted(kind).Contains(tag)) ?? row.Tags.Keys.FirstOrDefault(tag => Fitting(kind).Contains(tag)) ?? kind) + (row.ProviderFilled ? " · from " + (providerName ?? "your music service") : "");

    internal static IReadOnlyList<MixCandidate> Qualify(string kind, IReadOnlyList<MixCandidate> rows,
        IReadOnlyDictionary<string, AudioMuseScores> features)
    {
        var best = BestMoods(features);
        var result = new List<(MixCandidate Row, double Score)>();
        foreach (var row in rows)
        {
            features.TryGetValue(row.Song.IsLocal ? row.Song.Id : "", out var sound);
            var ranked = sound != null && best != null && best.ContainsKey(row.Song.Id);
            var measured = ranked || sound != null && HasMeasurements(kind, sound);
            // A style is only a hint, so it counts for songs the listener knows, not for picks from a service.
            var score = ranked ? best![row.Song.Id] is { } mood && mood.Kind == kind ? (double?)(1 + mood.Rank) : null :
                measured ? Score(kind, sound!) : TagScore(kind, row.Tags, fitting: row.Song.IsLocal || row.Plays > 0 || row.Loved);
            if (score != null) result.Add((row with { Source = measured ? "audiomuse" : row.Source }, score.Value));
        }
        // Songs that plainly carry the mood come before songs that only share a fitting style.
        return result.OrderByDescending(item => item.Score >= 1).Select(item => item.Row).ToList();
    }

    internal const int RankedMinimum = 30;
    internal const double RankedShare = .15;

    // AudioMuse's mood scores sit close together across a library, and some songs read high on every mood, so fixed
    // thresholds put the same songs in the party, chill and sleep mixes. With enough analyzed songs, each score is read
    // against the listener's other analyzed songs and against the song's own other scores, a song qualifies when it is
    // in the top share of a mood, and it joins only the mood it ranks highest in. Songs that fit no mood map to null.
    internal static IReadOnlyDictionary<string, (string Kind, double Rank)?>? BestMoods(IReadOnlyDictionary<string, AudioMuseScores> features)
    {
        var full = features.Select(item => (item.Key, Sound: item.Value, Values: Profile(item.Value))).Where(item => item.Values != null).ToArray();
        if (full.Length < RankedMinimum) return null;
        var mean = new double[6];
        var spread = new double[6];
        for (var i = 0; i < 6; i++)
        {
            mean[i] = full.Average(item => item.Values![i]);
            spread[i] = Math.Max(.01, Math.Sqrt(full.Average(item => Math.Pow(item.Values![i] - mean[i], 2))));
        }
        var fits = full.Select(item =>
        {
            var z = item.Values!.Select((value, i) => (value - mean[i]) / spread[i]).ToArray();
            var own = z.Average();
            return (item.Key, Fits: Fits(item.Sound, z.Select(value => value - own).ToArray()));
        }).ToArray();
        var sorted = Kinds.ToDictionary(kind => kind, kind => fits.Where(item => item.Fits.ContainsKey(kind))
            .Select(item => item.Fits[kind]).Order().ToArray());
        return fits.ToDictionary(item => item.Key, item =>
        {
            (string Kind, double Rank)? chosen = null;
            foreach (var kind in Kinds)
            {
                if (!item.Fits.TryGetValue(kind, out var fit) || fit <= 0) continue;
                var rank = (double)LowerBound(sorted[kind], fit) / sorted[kind].Length;
                if (rank >= 1 - RankedShare && (chosen == null || rank > chosen.Value.Rank)) chosen = (kind, rank);
            }
            return chosen;
        }, StringComparer.Ordinal);
    }

    // Relaxed, happy, sad, party, danceable and aggressive, when AudioMuse measured all six.
    private static double[]? Profile(AudioMuseScores s) =>
        s is { Relaxed: { } relaxed, Happy: { } happy, Sad: { } sad, Party: { } party, Danceable: { } danceable, Aggressive: { } aggressive }
            ? [relaxed, happy, sad, party, danceable, aggressive] : null;

    // Each fit averages how far the song leans toward the mood's sounds and away from its opposites; energy and tempo
    // limits still apply when they are known.
    private static Dictionary<string, double> Fits(AudioMuseScores s, double[] c)
    {
        var (relaxed, happy, sad, party, danceable, aggressive) = (c[0], c[1], c[2], c[3], c[4], c[5]);
        var fits = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["focus"] = (relaxed - aggressive - party) / 3,
            ["party"] = (party + danceable + happy - sad - relaxed) / 5
        };
        if (s.Energy == null || s.Energy is >= .2 and <= .7) fits["chill"] = (relaxed + happy - aggressive) / 3;
        if (s.Energy == null || s.Energy <= .35) fits["sleep"] = (relaxed + sad - party - danceable) / 4;
        if ((s.Energy == null || s.Energy >= .6) && (s.Tempo == null || s.Tempo is >= 120 and <= 170))
            fits["workout"] = (aggressive + danceable - relaxed) / 3;
        return fits;
    }

    private static int LowerBound(double[] sorted, double value)
    {
        var (low, high) = (0, sorted.Length);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static bool HasMeasurements(string kind, AudioMuseScores scores) => kind switch
    {
        "chill" => scores.Relaxed != null && scores.Happy != null,
        "focus" => scores.Relaxed != null && scores.Aggressive != null,
        "sleep" => scores.Relaxed != null && scores.Sad != null,
        "workout" => scores.Aggressive != null && scores.Danceable != null,
        _ => scores.Party != null && scores.Danceable != null && scores.Happy != null
    };

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
    internal static string Rule(string kind) => $"Analyzed library songs qualify when they are among the {RankedShare * 100:0}% of your analyzed songs that lean most toward " + (kind switch
    {
        "chill" => "relaxed and happy and away from aggressive, with energy 0.2–0.7 when known",
        "focus" => "relaxed and away from aggressive and party",
        "sleep" => "relaxed and sad and away from party and danceable, with energy ≤ 0.35 when known",
        "workout" => "aggressive and danceable and away from relaxed, with energy ≥ 0.6 and tempo 120–170 when known",
        _ => "party, danceable and happy and away from sad and relaxed"
    }) + $"; each song joins only the mood it leans toward most. With fewer than {RankedMinimum} analyzed songs, they require " + (kind switch
    {
        "chill" => "relaxed ≥ 0.6, happy ≥ 0.5; energy 0.2–0.7 when known.",
        "focus" => "relaxed ≥ 0.6, aggressive ≤ 0.4.",
        "sleep" => "relaxed ≥ 0.65, sad ≥ 0.5; energy ≤ 0.35 when known.",
        "workout" => "aggressive and danceable ≥ 0.6; energy ≥ 0.6 and tempo 120–170 when known.",
        _ => "party and danceable ≥ 0.6, happy ≥ 0.5."
    }) + " When required measurements are missing, one of these saved or evidenced tags: " + string.Join(", ", Wanted(kind)) + "." +
        (Fitting(kind) is { Length: > 0 } fitting ? " Library songs and service songs you played or saved also qualify in fitting styles: " + string.Join(", ", fitting) + "." : "");

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
