using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Subsonic;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    private async Task<List<object>> ReadOwnedArtistAlbumsAsync(string artistName, IReadOnlyList<Album> catalog,
        SubsonicRequestParameters parameters, string format)
    {
        var artistKey = FuzzyMatcher.NormalizeForMatching(artistName);
        if (artistKey.Length == 0 || catalog.Count == 0) return [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var request = SubsonicRequestParameters.FromDictionary(parameters.Select(SonicRelayParameters))
                .SetValue("f", format);
            var search = await _proxyService.RelayRawAsync("rest/search3.view", request
                .SetValue("query", artistName).SetValue("artistCount", "20")
                .SetValue("albumCount", "0").SetValue("songCount", "0"), deadline.Token);
            if (!search.IsSuccessStatusCode) return [];
            var candidates = _modelMapper.ParseSearchResponse(search.Body, search.ContentType).Artists
                .Select(row => SubsonicModelMapper.ToNativeItem(row, MusicItemKind.Artist))
                .Where(item => item.Id.Length > 0 && FuzzyMatcher.NormalizeForMatching(item.Name) == artistKey)
                .Take(5);
            foreach (var candidate in candidates)
            {
                var native = await _proxyService.RelayRawAsync("rest/getArtist.view", request.SetValue("id", candidate.Id), deadline.Token);
                if (!native.IsSuccessStatusCode) continue;
                var rows = _modelMapper.ParseSearchResponse(native.Body, native.ContentType, "artist").Albums;
                var albums = DescribeNativeAlbums(rows, artistName);
                // Require a shared release before adding a same-named library artist's albums.
                if (albums.Any(owned => catalog.Any(album => MusicAlbumReleases.Compatible(owned, album) &&
                        MusicAlbumReleases.FamilyKey(owned) == MusicAlbumReleases.FamilyKey(album))))
                    return rows;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Library albums for the service artist were unavailable ({ExceptionType})", error.GetType().Name);
        }
        return [];
    }
}
