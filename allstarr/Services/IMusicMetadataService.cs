using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;

namespace allstarr.Services;

public interface IMusicMetadataService
{
    Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches only providers that can currently supply audio.
    /// </summary>
    Task<List<Song>> SearchPlayableSongsAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        SearchSongsAsync(query, limit, cancellationToken);

    Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);

    Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);

    Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default);

    Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to find a song by ISRC using the provider's most exact lookup path.
    /// </summary>
    Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default);

    Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    // Providers return their popular/top-track surface, not a complete artist discography.
    Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);

    Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
}

public interface IConcreteMetadataService : IMusicMetadataService
{
    string ProviderId { get; }
}

internal static class ConcreteProviderId
{
    public static string Normalize(string providerId) => providerId.Trim().ToLowerInvariant() switch
    {
        "applemusic" => "apple-download",
        var normalized => normalized
    };
}
