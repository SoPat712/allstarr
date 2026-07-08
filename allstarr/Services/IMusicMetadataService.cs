using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;

namespace allstarr.Services;

/// <summary>
/// Interface for external music metadata search service
/// (Deezer API, Spotify API, MusicBrainz, etc.)
/// </summary>
public interface IMusicMetadataService
{
    /// <summary>
    /// Searches for songs on external providers
    /// </summary>
    /// <param name="query">Search term</param>
    /// <param name="limit">Maximum number of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of found songs</returns>
    Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Searches for albums on external providers
    /// </summary>
    Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Searches for artists on external providers
    /// </summary>
    Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Combined search (songs, albums, artists)
    /// </summary>
    Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets details of an external song
    /// </summary>
    Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to find a song by ISRC using the provider's most exact lookup path.
    /// </summary>
    Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets details of an external album with its songs
    /// </summary>
    Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets details of an external artist
    /// </summary>
    Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets an artist's albums
    /// </summary>
    Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets an artist's top tracks (not all songs, just popular tracks from the artist endpoint)
    /// </summary>
    Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Searches for playlists on external providers
    /// </summary>
    /// <param name="query">Search term</param>
    /// <param name="limit">Maximum number of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of found playlists</returns>
    Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets details of an external playlist (metadata only, not tracks)
    /// </summary>
    /// <param name="externalProvider">Provider name (e.g., "deezer", "qobuz")</param>
    /// <param name="externalId">Playlist ID from the provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Playlist details or null if not found</returns>
    Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all tracks from an external playlist
    /// </summary>
    /// <param name="externalProvider">Provider name (e.g., "deezer", "qobuz")</param>
    /// <param name="externalId">Playlist ID from the provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of songs in the playlist</returns>
    Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Marker interface to distinguish concrete metadata services from orchestrator services.
/// </summary>
public interface IConcreteMetadataService : IMusicMetadataService
{
}
