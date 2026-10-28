using System.Text.RegularExpressions;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

public static class CleanRecordingPolicy
{
    public static bool IsVerifiedClean(Song song) => song.ExplicitContentLyrics is 0 or 3;

    public static bool IsCounterpart(Song source, Song candidate)
    {
        if (!IsVerifiedClean(candidate) || source.Id == candidate.Id ||
            string.IsNullOrWhiteSpace(source.Artist) || string.IsNullOrWhiteSpace(source.Album) ||
            source.Duration is not > 0 || candidate.Duration is not > 0 ||
            Math.Abs(source.Duration.Value - candidate.Duration.Value) > 5) return false;
        return Key(source.Title) == Key(candidate.Title) && Key(source.Artist) == Key(candidate.Artist) &&
            Key(source.Album) == Key(candidate.Album) && Key(source.Title).Length > 0 &&
            Versions(source) == Versions(candidate);
    }

    private static string Versions(Song song) => string.Join('|',
        FuzzyMatcher.SemanticVersionTags(RemoveRating(string.Join(' ', song.Title, song.Album))).Order(StringComparer.Ordinal));

    private static string Key(string value) => FuzzyMatcher.NormalizeForMatching(RemoveRating(value));

    private static string RemoveRating(string value) => Regex.Replace(Regex.Replace(value,
        @"\s+\[(?:A|E)\](?:/\[(?:A|E)\])?$", ""),
        @"\s*[\(\[]\s*(?:clean|explicit|edited|clean version|explicit version|clean edit)\s*[\)\]]", "", RegexOptions.IgnoreCase);
}
