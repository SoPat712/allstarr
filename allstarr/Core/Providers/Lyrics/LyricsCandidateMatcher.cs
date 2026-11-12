using System.Text;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

internal static partial class LyricsCandidateMatcher
{
    internal static IEnumerable<ProviderLyricsRequest> Queries(ProviderLyricsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle)) { yield return request; yield break; }
        foreach (var reading in Readings(request.TrackTitle, request.ArtistNames))
            yield return new(request.CanonicalRecordingId, request.ProviderTrackId, request.AvailabilityOnly, request.PreferredFormat,
                reading.Title, reading.Artists, request.AlbumTitle, request.DurationSeconds, request.Isrc)
            { Refresh = request.Refresh, IsHealthProbe = request.IsHealthProbe };
    }

    internal static bool Matches(ProviderLyricsRequest request, string? title, string? artist, string? album = null) =>
        Matches(request.TrackTitle, request.ArtistNames, request.AlbumTitle, title, artist, album);

    internal static bool Matches(string? requestedTitle, IReadOnlyList<string> requestedArtists, string? requestedAlbum,
        string? title, string? artist, string? album)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(requestedTitle)) return false;
        if (Backing().IsMatch(title + " " + album) && !Backing().IsMatch(requestedTitle + " " + requestedAlbum)) return false;
        foreach (var expected in Readings(requestedTitle, requestedArtists))
            foreach (var actual in Readings(title, [artist]))
            {
                if (!Numbers().Matches(expected.Numbers).Select(match => match.Value)
                    .SequenceEqual(Numbers().Matches(actual.Numbers).Select(match => match.Value))) continue;
                var expectedArtists = string.Join(", ", expected.Artists);
                var actualArtists = string.Join(", ", actual.Artists);
                if (!FuzzyMatcher.TitleAnnotationsMatch(expected.Title, expectedArtists, actual.Title, actualArtists)) continue;
                var expectedName = CoreTitle(expected.Title, expectedArtists, actualArtists);
                var actualName = CoreTitle(actual.Title, actualArtists, expectedArtists);
                if (Similarity(expectedName, actualName) < 0.8) continue;
                var credits = actual.Artists.SelectMany(FuzzyMatcher.SplitCredits).ToArray();
                if (expected.Artists.Count > 0 && expected.Artists.All(name => credits.Any(credit => ArtistSimilarity(name, credit) >= 0.8) ||
                    ArtistSimilarity(name, actualArtists) >= 0.8)) return true;
            }
        return false;
    }

    private static string CoreTitle(string title, string artists, string others) => FuzzyMatcher.NormalizeTitleForMatching(
        Version().Replace(title, match => FuzzyMatcher.SemanticVersionTags(match.Groups[1].Value).Count > 0 ? " " : match.Value), artists, others);

    private static double ArtistSimilarity(string left, string right)
    {
        static string Sorted(string value) => string.Join(' ', FuzzyMatcher.NormalizeForMatching(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        return Math.Max(Similarity(left, right), Similarity(Sorted(left), Sorted(right)));
    }

    internal static IEnumerable<(string Title, IReadOnlyList<string> Artists, string Numbers)> Readings(string title, IReadOnlyList<string> artists)
    {
        yield return (title, artists, title);
        var split = Dash().Split(title, 2);
        if (split.Length != 2 || split.Any(string.IsNullOrWhiteSpace)) yield break;
        yield return (split[1], [split[0]], split[1]);
        yield return (split[0], artists, title);
    }

    internal static double Similarity(string left, string right)
    {
        var a = FuzzyMatcher.NormalizeForMatching(left).EnumerateRunes().ToArray();
        var b = FuzzyMatcher.NormalizeForMatching(right).EnumerateRunes().ToArray();
        if (a.Length == 0 || b.Length == 0) return 0;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
    [GeneratedRegex(@"\s+[-–—]\s+")]
    private static partial Regex Dash();
    [GeneratedRegex(@"[\(\[]([^\)\]]+)[\)\]]")]
    private static partial Regex Version();
    [GeneratedRegex(@"\b(?:instrumental|karaoke|backing[ -]+track)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Backing();
}
