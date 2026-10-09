using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;

namespace allstarr.Services.AppleMusic;

public sealed class AppleMusicMetadataService(AppleMusicKitMetadataCapabilityAdapter metadata,
    AppleMusicKitPlaylistCapabilityAdapter playlists) : IConcreteMetadataService
{
    public string ProviderId => AppleMusicClient.ProviderId;
    private ProviderExecutionContext Context(CancellationToken token) => new(new(ProviderActorKind.PublicRead, null),
        ProviderId, null, new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow, true, false, false, [ProviderId]), "catalog-read", "catalog-read",
        DateTimeOffset.UtcNow.AddSeconds(30), token);
    private ProviderExternalResourceId Id(string value, ProviderResourceKind kind) => new(ProviderId, kind, value);

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default) =>
        (await metadata.SearchTracksAsync(Context(cancellationToken), new(query, new(limit)))).RequireValue().Items.Select(ProtocolProviderGateway.Map).ToList();
    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default) =>
        (await metadata.SearchAlbumsAsync(Context(cancellationToken), new(query, new(limit)))).RequireValue().Items.Select(ProtocolProviderGateway.Map).ToList();
    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default) =>
        (await metadata.SearchArtistsAsync(Context(cancellationToken), new(query, new(limit)))).RequireValue().Items.Select(ProtocolProviderGateway.Map).ToList();
    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default) => new()
    {
        Songs = await SearchSongsAsync(query, songLimit, cancellationToken),
        Albums = await SearchAlbumsAsync(query, albumLimit, cancellationToken),
        Artists = await SearchArtistsAsync(query, artistLimit, cancellationToken)
    };
    public async Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        Track(await metadata.GetTrackAsync(Context(cancellationToken), new(Id(externalId, ProviderResourceKind.Track))));
    public async Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default) =>
        Track(await metadata.LookupByIsrcAsync(Context(cancellationToken), new(isrc)));
    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var result = await metadata.GetAlbumAsync(Context(cancellationToken), new(Id(externalId, ProviderResourceKind.Album)));
        return result.IsSuccess ? ProtocolProviderGateway.Map(result.RequireValue()) : null;
    }
    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var result = await metadata.GetArtistAsync(Context(cancellationToken), new(Id(externalId, ProviderResourceKind.Artist)));
        return result.IsSuccess ? ProtocolProviderGateway.Map(result.RequireValue()) : null;
    }
    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var result = new List<Album>();
        string? cursor = null;
        do
        {
            var page = (await metadata.GetArtistAlbumsAsync(Context(cancellationToken), new(Id(externalId, ProviderResourceKind.Artist), new(100, cursor)))).RequireValue();
            result.AddRange(page.Items.Select(ProtocolProviderGateway.Map));
            cursor = page.NextCursor;
        } while (cursor != null && result.Count < 20_000);
        return result;
    }
    public async Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        (await metadata.GetArtistTracksAsync(Context(cancellationToken), new(Id(externalId, ProviderResourceKind.Artist), new(100)))).RequireValue().Items.Select(ProtocolProviderGateway.Map).ToList();
    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default) =>
        (await playlists.SearchPlaylistsAsync(Context(cancellationToken), new(query, new(limit)))).RequireValue().Items.Select(ProtocolProviderGateway.Map).ToList();
    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var result = await playlists.GetPlaylistTracksAsync(Context(cancellationToken), new(Id(externalId, ProviderResourceKind.Playlist), new(1)));
        return result.IsSuccess ? ProtocolProviderGateway.Map(result.RequireValue().Playlist) : null;
    }
    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var result = new List<Song>();
        string? cursor = null;
        for (var index = 0; index < 1000; index++)
        {
            var page = (await playlists.GetPlaylistTracksAsync(Context(cancellationToken), new(Id(externalId, ProviderResourceKind.Playlist), new(25, cursor)))).RequireValue();
            result.AddRange(page.Tracks.Items.Where(item => item.Metadata != null).Select(item => ProtocolProviderGateway.Map(item.Metadata!)));
            cursor = page.Tracks.NextCursor;
            if (cursor == null) return result;
        }
        throw new InvalidDataException("The Apple playlist exceeded its page limit.");
    }
    private static Song? Track(ProviderOutcome<ProviderTrackMetadata> result) => result.IsSuccess ? ProtocolProviderGateway.Map(result.RequireValue()) : null;
}
