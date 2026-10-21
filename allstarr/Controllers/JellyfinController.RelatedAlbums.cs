using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<IActionResult> GetRelatedProviderAlbumsAsync(string itemId, string provider, string externalId, int limit)
    {
        limit = Math.Clamp(limit, 0, 200);
        var items = new List<Dictionary<string, object?>>();
        if (limit == 0) return RelatedAlbumsResponse(items);
        var context = HttpContext.RequireProtocolExecutionContext() with { IsReadOnly = true };
        var excludedItems = RelatedExclusions("ExcludeItemIds");
        excludedItems.Add(itemId);
        var excludedArtists = RelatedExclusions("ExcludeArtistIds");
        try
        {
            var album = await _providerGateway.GetStoredAlbumAsync(context, provider, externalId);
            if (album == null) return RelatedAlbumsResponse(items);
            var artists = MusicDescriptiveMetadata.AlbumArtists(album).Where(artist => !string.IsNullOrWhiteSpace(artist.Id)).ToList();
            var related = new List<Artist>();
            foreach (var artist in artists.Take(20))
            {
                var (external, artistProvider, type, artistId) = _localLibraryService.ParseExternalId(artist.Id);
                if (external && type == "artist")
                    related.AddRange(await _providerGateway.GetSimilarArtistsAsync(context, artistProvider!, artistId!, 20));
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artist in artists.Concat(related).DistinctBy(artist => artist.Id).Take(40))
            {
                if (excludedArtists.Contains(artist.Id)) continue;
                var (external, artistProvider, type, artistId) = _localLibraryService.ParseExternalId(artist.Id);
                if (!external || type != "artist") continue;
                var albums = await _providerGateway.GetArtistAlbumsAsync(context, artistProvider!, artistId!);
                foreach (var candidate in albums)
                {
                    if (excludedItems.Contains(candidate.Id) || string.IsNullOrWhiteSpace(candidate.Id) ||
                        MusicDescriptiveMetadata.AlbumArtists(candidate).Any(credit => excludedArtists.Contains(credit.Id)) ||
                        !seen.Add(candidate.Id)) continue;
                    items.Add(_responseBuilder.ConvertAlbumToJellyfinItem(candidate));
                    if (items.Count == limit) return RelatedAlbumsResponse(items);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Related albums for {Provider} were unavailable ({ExceptionType})", provider, error.GetType().Name);
        }
        return RelatedAlbumsResponse(items);
    }

    private IActionResult RelatedAlbumsResponse(List<Dictionary<string, object?>> items) =>
        _responseBuilder.CreateJsonResponse(new { Items = items, TotalRecordCount = items.Count, StartIndex = 0 });

    private HashSet<string> RelatedExclusions(string key) => Request.Query[key]
        .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToHashSet(StringComparer.Ordinal);
}
