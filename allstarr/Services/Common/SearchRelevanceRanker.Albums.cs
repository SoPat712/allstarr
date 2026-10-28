using allstarr.Models.Domain;
using System.Text.RegularExpressions;

namespace allstarr.Services.Common;

public static partial class SearchRelevanceRanker
{
    public static double ScoreAlbum(string query, Album album) => Math.Max(
        Score(query, album.Title, album.Artist),
        Score(query, AlbumContentLabelRegex().Replace(album.Title, ""), album.Artist));

    [GeneratedRegex(@"\s*[\(\[](?:explicit|clean)[\)\]]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlbumContentLabelRegex();
}
