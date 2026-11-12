using allstarr.Models.Domain;
using System.Text.RegularExpressions;

namespace allstarr.Services.Common;

public static partial class SearchRelevanceRanker
{
    public static double ScoreAlbum(string query, Album album, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Math.Max(Score(query, album.Title, album.Artist, cancellationToken: cancellationToken),
            Score(query, AlbumContentLabelRegex().Replace(album.Title, ""), album.Artist, cancellationToken: cancellationToken));
    }

    [GeneratedRegex(@"\s*[\(\[](?:explicit|clean)[\)\]]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlbumContentLabelRegex();
}
