using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

public sealed partial class MusicSearchService
{
    /// <summary>
    /// Same-named artists from two services are one artist when a song credited to that name is the same recording on
    /// both: the same ISRC, or the same title, length and album. Without such a song both stay, so no artist is lost.
    /// </summary>
    internal static bool SameServiceArtist(Artist first, Artist second, IReadOnlyCollection<Song> songs)
    {
        if (ArtistKey(first) is null || ArtistKey(second) is null) return false;
        var name = FuzzyMatcher.NormalizeForMatching(first.Name);
        if (name.Length == 0 || name != FuzzyMatcher.NormalizeForMatching(second.Name)) return false;
        var firstService = Service(first.ExternalProvider);
        var secondService = Service(second.ExternalProvider);
        if (firstService == secondService) return false;
        bool CreditedOn(Song song, string service) => Service(song.ExternalProvider) == service &&
            FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(song.Artist).FirstOrDefault() ?? "") == name;
        var left = songs.Where(song => CreditedOn(song, firstService)).ToArray();
        if (left.Length == 0) return false;
        return songs.Where(song => CreditedOn(song, secondService)).Any(right => left.Any(song =>
            NormalizeIdentifier(song.Isrc) is { } isrc && isrc == NormalizeIdentifier(right.Isrc) || MetadataRecordingsMatch(song, right)));
    }

    /// <summary>Every service artist, except a same-named one that songs prove is an artist already kept.</summary>
    internal static List<Artist> WithoutProvenServiceRepeats(IEnumerable<Artist> artists, IReadOnlyCollection<Song> songs)
    {
        var kept = new List<Artist>();
        foreach (var artist in artists)
            if (!kept.Any(other => SameServiceArtist(other, artist, songs))) kept.Add(artist);
        return kept;
    }

    private static string Service(string? provider) => AppleMusicClient.NormalizeProviderId(provider ?? "");
}
