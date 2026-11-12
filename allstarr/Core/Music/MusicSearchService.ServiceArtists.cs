using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

public sealed partial class MusicSearchService
{
    /// <summary>
    /// One streaming artist per name: same-named service artists after the first fold into it. Its page brings in the
    /// other services' albums when they share one, and a wrong fold is split with "Not the same".
    /// </summary>
    internal static List<Artist> OneServiceArtistPerName(IEnumerable<Artist> artists)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return artists.Where(artist => ArtistKey(artist) == null || names.Add(FuzzyMatcher.NormalizeForMatching(artist.Name))).ToList();
    }
}
