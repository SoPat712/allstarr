using allstarr.Models.Domain;
using allstarr.Models.Search;

namespace allstarr.Services.Common;

/// <summary>
/// Delegation wrapper that forwards search calls to MultiProviderMetadataService.
/// Keeps class signature intact to avoid breaking DI dependency bindings in other controllers.
/// </summary>
public class ParallelMetadataService
{
    private readonly IMusicMetadataService _metadataService;

    public ParallelMetadataService(IMusicMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default)
    {
        return await _metadataService.SearchAllAsync(query, songLimit, albumLimit, artistLimit, cancellationToken);
    }

    public async Task<Song?> SearchSongAsync(string title, string artist, int limit = 5, CancellationToken cancellationToken = default)
    {
        var songs = await _metadataService.SearchSongsAsync($"{title} {artist}", limit, cancellationToken);
        return songs.FirstOrDefault();
    }
}
