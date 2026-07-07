using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.Common;

public class MultiProviderMetadataService : IMusicMetadataService
{
    private readonly IEnumerable<IMusicMetadataService> _allServices;
    private readonly ProviderStatusManager _statusManager;
    private readonly ExtensionManager _extensionManager;
    private readonly ILogger<MultiProviderMetadataService> _logger;

    public MultiProviderMetadataService(
        IEnumerable<IMusicMetadataService> services,
        ProviderStatusManager statusManager,
        ExtensionManager extensionManager,
        ILogger<MultiProviderMetadataService> logger)
    {
        _allServices = services.Where(s => s.GetType() != typeof(MultiProviderMetadataService)).ToList();
        _statusManager = statusManager;
        _extensionManager = extensionManager;
        _logger = logger;
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();
        
        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Song>();
            try
            {
                return await service.SearchSongsAsync(query, limit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchSongsAsync failed for provider: {Provider}", p);
                return new List<Song>();
            }
        }).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(async ext =>
        {
            try
            {
                var res = await Task.Run(() => ext.Search(query, limit), cancellationToken);
                return res.Songs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchSongsAsync failed for extension: {ExtensionId}", ext.Id);
                return new List<Song>();
            }
        });

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);

        var allResultsList = providerResults.Concat(extensionResults).ToList();
        return InterleaveLists(allResultsList);
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Album>();
            try
            {
                return await service.SearchAlbumsAsync(query, limit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAlbumsAsync failed for provider: {Provider}", p);
                return new List<Album>();
            }
        }).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(async ext =>
        {
            try
            {
                var res = await Task.Run(() => ext.Search(query, limit), cancellationToken);
                return res.Albums;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAlbumsAsync failed for extension: {ExtensionId}", ext.Id);
                return new List<Album>();
            }
        });

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);

        var allResultsList = providerResults.Concat(extensionResults).ToList();
        return InterleaveLists(allResultsList);
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Artist>();
            try
            {
                return await service.SearchArtistsAsync(query, limit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchArtistsAsync failed for provider: {Provider}", p);
                return new List<Artist>();
            }
        }).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(async ext =>
        {
            try
            {
                var res = await Task.Run(() => ext.Search(query, limit), cancellationToken);
                return res.Artists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchArtistsAsync failed for extension: {ExtensionId}", ext.Id);
                return new List<Artist>();
            }
        });

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);

        var allResultsList = providerResults.Concat(extensionResults).ToList();
        return InterleaveLists(allResultsList);
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return null;
            try
            {
                return await service.SearchAllAsync(query, songLimit, albumLimit, artistLimit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAllAsync failed for provider: {Provider}", p);
                return null;
            }
        }).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(async ext =>
        {
            try
            {
                return await Task.Run(() => ext.Search(query, songLimit), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAllAsync failed for extension: {ExtensionId}", ext.Id);
                return new SearchResult();
            }
        });

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);

        var validProviderResults = providerResults.Where(r => r != null).ToList();
        var validExtensionResults = extensionResults.Where(r => r != null).ToList();

        var allSongsLists = validProviderResults.Select(r => r!.Songs)
            .Concat(validExtensionResults.Select(r => r.Songs)).ToList();

        var allAlbumsLists = validProviderResults.Select(r => r!.Albums)
            .Concat(validExtensionResults.Select(r => r.Albums)).ToList();

        var allArtistsLists = validProviderResults.Select(r => r!.Artists)
            .Concat(validExtensionResults.Select(r => r.Artists)).ToList();

        return new SearchResult
        {
            Songs = InterleaveLists(allSongsLists),
            Albums = InterleaveLists(allAlbumsLists),
            Artists = InterleaveLists(allArtistsLists)
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var ext = _extensionManager.GetExtension(externalProvider);
        if (ext != null)
        {
            return await Task.Run(() => ext.GetSong(externalId), cancellationToken);
        }

        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetSongAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();
        
        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return null;
            try
            {
                return await service.FindSongByIsrcAsync(isrc, cancellationToken);
            }
            catch
            {
                return null;
            }
        }).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(async ext =>
        {
            try
            {
                var res = await Task.Run(() => ext.Search($"isrc:{isrc}", 1), cancellationToken);
                return res.Songs.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        var extResults = await Task.WhenAll(extensionTasks);

        return results.FirstOrDefault(s => s != null) ?? extResults.FirstOrDefault(s => s != null);
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var ext = _extensionManager.GetExtension(externalProvider);
        if (ext != null)
        {
            return await Task.Run(() => ext.GetAlbum(externalId), cancellationToken);
        }

        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetAlbumAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var ext = _extensionManager.GetExtension(externalProvider);
        if (ext != null)
        {
            return await Task.Run(() => ext.GetArtist(externalId), cancellationToken);
        }

        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetArtistAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var ext = _extensionManager.GetExtension(externalProvider);
        if (ext != null) return new List<Album>(); // Extensions don't have separate artist-albums endpoint usually

        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return new List<Album>();
        return await service.GetArtistAlbumsAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return new List<Song>();
        return await service.GetArtistTracksAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledPlaylistProviders();
        if (providers.Count == 0) return new List<ExternalPlaylist>();

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<ExternalPlaylist>();
            try
            {
                return await service.SearchPlaylistsAsync(query, limit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchPlaylistsAsync failed for provider: {Provider}", p);
                return new List<ExternalPlaylist>();
            }
        });

        var results = await Task.WhenAll(tasks);
        return InterleaveLists(results.ToList());
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetPlaylistAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return new List<Song>();
        return await service.GetPlaylistTracksAsync(externalProvider, externalId, cancellationToken);
    }

    private IMusicMetadataService? GetMetadataServiceByName(string name)
    {
        var normalizedName = name.ToLowerInvariant();
        return _allServices.FirstOrDefault(s =>
            s.GetType().Name.StartsWith(normalizedName, StringComparison.OrdinalIgnoreCase) ||
            (normalizedName == "squidwtf" && s.GetType().Name.StartsWith("SquidWTF", StringComparison.OrdinalIgnoreCase)) ||
            (normalizedName == "applemusic" && s.GetType().Name.StartsWith("AppleMusic", StringComparison.OrdinalIgnoreCase))
        );
    }

    private List<T> InterleaveLists<T>(List<List<T>> lists)
    {
        var result = new List<T>();
        var nonNullLists = lists.Where(l => l != null && l.Count > 0).ToList();
        if (nonNullLists.Count == 0) return result;

        int maxCount = nonNullLists.Max(l => l.Count);
        for (int i = 0; i < maxCount; i++)
        {
            foreach (var list in nonNullLists)
            {
                if (i < list.Count)
                {
                    result.Add(list[i]);
                }
            }
        }
        return result;
    }
}
