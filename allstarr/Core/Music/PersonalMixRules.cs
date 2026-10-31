using allstarr.Models.Domain;

namespace allstarr.Core.Music;

public sealed record MixReadiness(bool Ready, string Message);
internal sealed record MixCandidate(Song Song, IReadOnlyDictionary<string, double> Tags, int Plays, int RecentPlays,
    DateTimeOffset? LastPlayed, bool EarlySkipped, string Source, double Score, DateTimeOffset? ReleasedAt = null,
    bool Heard = false, bool Loved = false);

internal static class PersonalMixRules
{
    // Qualified plays include imports. Daily selection uses exact 7:3 batches, up to 50 songs.
    internal const int RequiredPlays = 200, RepeatPlays = 30, ArtistLimit = 2, TopSongs = 300;
    internal const int SkipDays = 7, RefreshMinutes = 15, ReleaseDays = 14;
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

    internal static MixReadiness Readiness(string kind, int plays, int recent, bool source, bool artists, bool old)
    {
        if (kind == "on-repeat" && recent < RepeatPlays) return new(false, $"Play about {RepeatPlays - recent} more songs in 30 days.");
        if ((kind.StartsWith("daily-", StringComparison.Ordinal) || kind == "discover-weekly") && plays < RequiredPlays)
            return new(false, $"Play about {RequiredPlays - plays} more songs, or import your listening history.");
        if (kind == "discover-weekly" && !source) return new(false, "Connect a service with recommendations, or add unheard tagged songs to your library.");
        if (kind == "release-radar" && !artists) return new(false, "Follow artists, or play an artist at least five times.");
        if (kind == "rewind" && !old) return new(false, "Import history with loved songs last played at least six months ago.");
        return new(true, "Ready");
    }

    internal static IReadOnlyList<MixCandidate> Select(string kind, IReadOnlyList<MixCandidate> candidates,
        DateTimeOffset now, IReadOnlyCollection<string> previous, int? length = null)
    {
        var rows = candidates.Where(item => !item.EarlySkipped).DistinctBy(item => Key(item.Song)).Where(item => kind switch
        {
            "on-repeat" => item.RecentPlays > 0,
            "rewind" => (item.Plays >= 5 || item.Loved) && item.LastPlayed <= now.AddMonths(-6),
            "discover-weekly" => !item.Heard && !item.Loved && item.Plays == 0 && !IdentityKeys(item.Song).Any(previous.Contains),
            "release-radar" => item.ReleasedAt >= now.AddDays(-ReleaseDays) && item.ReleasedAt <= now,
            _ => true
        }).OrderByDescending(item => kind == "on-repeat" ? item.RecentPlays : 0)
            .ThenByDescending(item => item.Score).ThenBy(item => item.Song.Id, StringComparer.Ordinal).ToArray();
        List<MixCandidate> Take(int familiar, int fresh)
        {
            var selected = new List<MixCandidate>();
            var artists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (familiar >= 0 && (row.Plays > 0 || row.Loved ? familiar : fresh) == 0) continue;
                var names = row.Song.Artists.Append(row.Song.Artist).Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (names.Any(name => artists.GetValueOrDefault(name) >= ArtistLimit)) continue;
                selected.Add(row);
                foreach (var name in names) artists[name] = artists.GetValueOrDefault(name) + 1;
                if (familiar >= 0) { if (row.Plays > 0 || row.Loved) familiar--; else fresh--; }
                if (selected.Count == (length ?? (kind.StartsWith("daily-", StringComparison.Ordinal) ? 50 : 30))) break;
            }
            return selected;
        }
        if (!kind.StartsWith("daily-", StringComparison.Ordinal)) return Take(-1, -1);
        for (var size = 50; size >= 10; size -= 10)
        {
            var selected = Take(size * 7 / 10, size * 3 / 10);
            if (selected.Count == size) return selected;
        }
        return [];
    }

    internal static string Key(Song song) => song.CanonicalRecordingId?.ToString("D") ?? song.Id;
    internal static IEnumerable<string> IdentityKeys(Song song) => new[] { Key(song), song.Id,
        "isrc:" + song.Isrc?.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant(),
        "track:" + allstarr.Services.Common.FuzzyMatcher.NormalizeForMatching(song.Title) + ":" + allstarr.Services.Common.FuzzyMatcher.NormalizeForMatching(song.Artist) }
        .Where(key => key != "isrc:");
    internal static double Distance(MixCandidate a, MixCandidate b)
    {
        var dot = a.Tags.Sum(tag => tag.Value * b.Tags.GetValueOrDefault(tag.Key));
        var norm = Math.Sqrt(a.Tags.Sum(tag => tag.Value * tag.Value) * b.Tags.Sum(tag => tag.Value * tag.Value));
        return norm == 0 ? 1 : Math.Clamp(1 - dot / norm, 0, 1);
    }

    internal static IReadOnlyList<IReadOnlyList<MixCandidate>> Cluster(IReadOnlyList<MixCandidate> candidates)
    {
        var rows = candidates.Where(item => (item.Plays > 0 || item.Loved) && item.Tags.Values.Any(value => value > 0))
            .OrderByDescending(item => item.Plays).ThenBy(item => item.Song.Id, StringComparer.Ordinal).Take(TopSongs).ToArray();
        if (rows.Length < 2) return [];
        var count = Math.Clamp(rows.Length / 75, 2, 4);
        var medoids = new List<MixCandidate> { rows[0] };
        while (medoids.Count < count)
        {
            var farthest = rows.OrderByDescending(row => medoids.Min(medoid => Distance(row, medoid)))
                .ThenBy(row => row.Song.Id, StringComparer.Ordinal).First();
            if (medoids.Min(medoid => Distance(farthest, medoid)) < .000001) break;
            medoids.Add(farthest);
        }
        IReadOnlyList<IReadOnlyList<MixCandidate>> groups = [];
        // Eight bounded medoid updates; all distance/cost ties use stable song identifiers.
        for (var iteration = 0; iteration < 8; iteration++)
        {
            groups = medoids.Select(medoid => (IReadOnlyList<MixCandidate>)rows.Where(row =>
                medoids.OrderBy(other => Distance(row, other)).ThenBy(other => other.Song.Id, StringComparer.Ordinal).First() == medoid).ToArray()).ToArray();
            var next = groups.Where(group => group.Count > 0).Select(group => group.OrderBy(row => group.Sum(other => Distance(row, other)))
                .ThenBy(row => row.Song.Id, StringComparer.Ordinal).First()).ToList();
            if (medoids.SequenceEqual(next)) break;
            medoids = next;
        }
        return groups.Where(group => group.Count > 0).ToArray();
    }

    internal static string ClusterName(IReadOnlyList<MixCandidate> group)
    {
        var tag = group.SelectMany(item => item.Tags).GroupBy(item => item.Key).OrderByDescending(items => items.Sum(item => item.Value))
            .ThenBy(items => items.Key, StringComparer.Ordinal).First().Key;
        var artists = group.GroupBy(item => item.Song.Artist).OrderByDescending(items => items.Count())
            .ThenBy(items => items.Key, StringComparer.Ordinal).Take(3).Select(items => items.Key);
        return tag + " · " + string.Join(", ", artists);
    }
}
