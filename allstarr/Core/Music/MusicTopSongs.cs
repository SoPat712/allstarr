using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

/// <summary>
/// Chooses provider songs that extend an artist's top songs from the library: songs credited
/// to that artist alone or as the leading credit, whose title is not already listed.
/// </summary>
public static class MusicTopSongs
{
    public static IReadOnlyList<Song> Extend(
        string artist,
        IEnumerable<string> listedTitles,
        IEnumerable<Song> providerSongs,
        int count)
    {
        var artistKey = FuzzyMatcher.NormalizeForMatching(artist);
        if (artistKey.Length == 0 || count <= 0) return [];
        var titles = listedTitles.Select(FuzzyMatcher.NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
        var added = new List<Song>();
        foreach (var song in providerSongs)
        {
            if (added.Count >= count) break;
            if (!IsLedBy(song.Artist, artistKey)) continue;
            if (!titles.Add(FuzzyMatcher.NormalizeForMatching(song.Title))) continue;
            added.Add(song);
        }
        return added;
    }

    // "Grimes" does not match "Gary Grimes", but does match "Grimes feat. i_o".
    private static bool IsLedBy(string? credit, string artistKey)
    {
        if (string.IsNullOrWhiteSpace(credit)) return false;
        if (FuzzyMatcher.NormalizeForMatching(credit) == artistKey) return true;
        var lead = FuzzyMatcher.SplitCredits(credit).FirstOrDefault();
        return lead != null && FuzzyMatcher.NormalizeForMatching(lead) == artistKey;
    }
}
