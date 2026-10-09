using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services;

namespace allstarr.Core.Providers;

public abstract class ConcreteMetadataCapabilityAdapter(string providerId, IConcreteMetadataService legacy)
    : ProviderCatalogMetadata(providerId)
{
    public override Task<List<Song>> SearchSongsAsync(
        string query, int limit = 20, CancellationToken cancellationToken = default) =>
        legacy.SearchSongsAsync(query, limit, cancellationToken);

    public override Task<List<Album>> SearchAlbumsAsync(
        string query, int limit = 20, CancellationToken cancellationToken = default) =>
        legacy.SearchAlbumsAsync(query, limit, cancellationToken);

    public override Task<List<Artist>> SearchArtistsAsync(
        string query, int limit = 20, CancellationToken cancellationToken = default) =>
        legacy.SearchArtistsAsync(query, limit, cancellationToken);

    public override Task<Song?> GetSongAsync(
        string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        legacy.GetSongAsync(externalProvider, externalId, cancellationToken);

    public override Task<Song?> FindSongByIsrcAsync(
        string isrc, CancellationToken cancellationToken = default) =>
        legacy.FindSongByIsrcAsync(isrc, cancellationToken);

    public override Task<Album?> GetAlbumAsync(
        string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        legacy.GetAlbumAsync(externalProvider, externalId, cancellationToken);

    public override Task<Artist?> GetArtistAsync(
        string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        legacy.GetArtistAsync(externalProvider, externalId, cancellationToken);

    public override Task<List<Album>> GetArtistAlbumsAsync(
        string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        legacy.GetArtistAlbumsAsync(externalProvider, externalId, cancellationToken);

    public override Task<List<Song>> GetArtistTracksAsync(
        string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        legacy.GetArtistTracksAsync(externalProvider, externalId, cancellationToken);

    public override Task<List<ExternalPlaylist>> SearchPlaylistsAsync(
        string query, int limit = 20, CancellationToken cancellationToken = default) =>
        legacy.SearchPlaylistsAsync(query, limit, cancellationToken);

    public override Task<ExternalPlaylist?> GetPlaylistAsync(
        string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        legacy.GetPlaylistAsync(externalProvider, externalId, cancellationToken);

    public override Task<List<Song>> GetPlaylistTracksAsync(
        string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        legacy.GetPlaylistTracksAsync(externalProvider, externalId, cancellationToken);

}

public class ConcretePlaylistCapabilityAdapter : CatalogPlaylistCapability
{
    public ConcretePlaylistCapabilityAdapter(string providerId, IConcreteMetadataService legacy,
        ConcreteMetadataCapabilityAdapter metadata) : base(providerId, metadata)
    {
        ArgumentNullException.ThrowIfNull(legacy);
    }
}
