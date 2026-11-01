using allstarr.Models.Domain;

namespace allstarr.Core.Music;

public sealed record MixReadiness(bool Ready, string Message);
internal sealed record MixCandidate(Song Song, IReadOnlyDictionary<string, double> Tags, int Plays, int RecentPlays,
    DateTimeOffset? LastPlayed, bool EarlySkipped, string Source, double Score, DateTimeOffset? ReleasedAt = null,
    bool Heard = false, bool Loved = false, int NinetyPlays = 0, bool ProviderFilled = false,
    string? RelatedTo = null, string? EvidenceTag = null, string? EvidenceSource = null, string? ReleaseKey = null, bool Less = false, string[]? ChartTags = null);

internal static class PersonalMixRules
{
    // Qualified completed plays include enabled imports.
    internal const int RepeatPlays = MixRecipes.RecentDays, TopSongs = 300;
    internal const int SkipDays = MixRecipes.RecentDays, RefreshMinutes = 15, ReleaseDays = MixRecipes.ReleaseDays;
    internal static readonly string[] Kinds = ["on-repeat", "rewind", "release-radar", "discover-weekly", "daily-1", "daily-2", "daily-3", "daily-4", .. PersonalMixMoods.Kinds];
    internal static string Name(string kind, string? style = null)
    {
        if (kind.StartsWith("daily-", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(style)) return "Blend " + kind[^1];
            var label = style.Trim().Equals("r&b", StringComparison.OrdinalIgnoreCase) ? "R&B"
                : style.Trim().Equals("edm", StringComparison.OrdinalIgnoreCase) ? "EDM"
                : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(style.Trim());
            if (label.StartsWith("Uk ", StringComparison.Ordinal)) label = "UK" + label[2..];
            return label + " Blend";
        }
        return PersonalMixMoods.Kinds.Contains(kind) ? char.ToUpperInvariant(kind[0]) + kind[1..] + " Mix" : kind switch
        { "on-repeat" => "Heavy Rotation", "rewind" => "Long Lost", "release-radar" => "Just Dropped", _ => "Uncharted" };
    }
    internal static string Cron(string kind) => PersonalMixMoods.Kinds.Contains(kind) ? "0 4 * * 1" : kind switch
    { "release-radar" => "0 4 * * 5", "discover-weekly" or "rewind" => "0 4 * * 1", _ => "0 4 * * *" };

    internal static IReadOnlyList<MixCandidate> Select(string kind, IReadOnlyList<MixCandidate> candidates,
        DateTimeOffset now, IReadOnlyCollection<string> previous, int? length = null)
    {
        var recipe = MixRecipes.For(kind);
        var rows = candidates.Where(item => !item.EarlySkipped).DistinctBy(item => Key(item.Song)).Where(item => kind switch
        {
            "on-repeat" => item.RecentPlays > 0 || item.NinetyPlays > 0,
            "rewind" => item.Plays >= recipe.MinimumPlays && item.LastPlayed <= now.AddMonths(-recipe.InactiveMonths),
            "discover-weekly" => !item.Heard && !item.Loved && item.Plays == 0 && !IdentityKeys(item.Song).Any(previous.Contains),
            "release-radar" => item.ReleasedAt >= now.AddDays(-recipe.WindowDays) && item.ReleasedAt <= now,
            _ => true
        }).OrderBy(item => kind == "release-radar" ? false : item.ProviderFilled).ThenBy(item => item.Less)
            .ThenBy(item => kind == "on-repeat" && item.RecentPlays == 0 ? 1 : 0)
            .ThenByDescending(item => kind == "release-radar" ? item.ReleasedAt : null)
            .ThenByDescending(item => kind == "on-repeat" ? item.RecentPlays > 0 ? item.RecentPlays : item.NinetyPlays : kind == "custom" || kind == "release-radar" ? 0 : item.Plays)
            .ThenByDescending(item => kind == "custom" ? item.Score : 0)
            .ThenBy(item => item.ReleaseKey, StringComparer.Ordinal)
            .ThenBy(item => kind == "release-radar" ? item.Song.Track ?? int.MaxValue : 0).ThenBy(item => item.Song.Id, StringComparer.Ordinal);
        var selected = new List<MixCandidate>();
        var artists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var releases = new HashSet<string>(StringComparer.Ordinal);
        var familiar = 0; var fresh = 0;
        foreach (var row in rows)
        {
            var known = row.Plays > 0 || row.Loved;
            if (kind.StartsWith("daily-", StringComparison.Ordinal) && (known ? familiar : fresh) >= recipe.Target / 2) continue;
            var names = row.Song.Artists.Append(row.Song.Artist).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Any(name => artists.GetValueOrDefault(name) >= recipe.ArtistLimit ||
                row.ProviderFilled && fills.GetValueOrDefault(name) >= 2)) continue;
            if (kind == "release-radar" && row.ReleaseKey != null && !releases.Add(row.ReleaseKey)) continue;
            selected.Add(row);
            foreach (var name in names)
            {
                artists[name] = artists.GetValueOrDefault(name) + 1;
                if (row.ProviderFilled) fills[name] = fills.GetValueOrDefault(name) + 1;
            }
            if (known) familiar++; else fresh++;
            if (selected.Count == (length ?? recipe.Target)) break;
        }
        return selected;
    }

    internal static string Key(Song song) => song.CanonicalRecordingId?.ToString("D") ?? song.Id;
    internal static IEnumerable<string> IdentityKeys(Song song) => new[] { Key(song), song.Id,
        "isrc:" + song.Isrc?.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant(),
        "track:" + allstarr.Services.Common.FuzzyMatcher.NormalizeForMatching(song.Title) + ":" + allstarr.Services.Common.FuzzyMatcher.NormalizeForMatching(song.Artist) }
        .Where(key => key != "isrc:");
}
