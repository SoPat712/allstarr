using System.Text;
using Microsoft.Extensions.Options;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using IOFile = System.IO.File;

namespace allstarr.Services.Subsonic;

public sealed class PlaylistSyncService
{
    private readonly IReadOnlyDictionary<string, IConcreteMetadataService> _metadataServices;
    private readonly IReadOnlyDictionary<string, IConcreteDownloadService> _downloadServices;
    private readonly IConfiguration _configuration;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<PlaylistSyncService> _logger;

    private readonly string _musicDirectory;
    private readonly string _playlistDirectory;

    public PlaylistSyncService(
        IEnumerable<IConcreteMetadataService> metadataServices,
        IEnumerable<IConcreteDownloadService> downloadServices,
        IConfiguration configuration,
        IOptions<SubsonicSettings> subsonicSettings,
        ILogger<PlaylistSyncService> logger)
    {
        _metadataServices = metadataServices.ToDictionary(
            service => service.ProviderId,
            StringComparer.Ordinal);
        _downloadServices = downloadServices.ToDictionary(
            service => service.ProviderId,
            StringComparer.Ordinal);
        _configuration = configuration;
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;

        _musicDirectory = configuration["Library:DownloadPath"] ?? "./downloads";
        _playlistDirectory = Path.Combine(_musicDirectory, _subsonicSettings.PlaylistsDirectory ?? "playlists");

        Directory.CreateDirectory(_playlistDirectory);
    }

    private IConcreteMetadataService? GetMetadataServiceForProvider(string provider) =>
        _metadataServices.GetValueOrDefault(ConcreteProviderId.Normalize(provider));

    private IConcreteDownloadService? GetDownloadServiceForProvider(string provider) =>
        _downloadServices.GetValueOrDefault(ConcreteProviderId.Normalize(provider));

    public async Task DownloadFullPlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting download for playlist {PlaylistId}", playlistId);

            if (!PlaylistIdHelper.IsExternalPlaylist(playlistId))
            {
                _logger.LogWarning("Invalid playlist ID format: {PlaylistId}", playlistId);
                return;
            }

            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

            var metadataService = GetMetadataServiceForProvider(provider);
            if (metadataService == null)
            {
                throw new NotSupportedException($"Provider '{provider}' not supported for playlists");
            }

            var playlist = await metadataService.GetPlaylistAsync(provider, externalId);
            if (playlist == null)
            {
                _logger.LogWarning("Playlist not found: {PlaylistId}", playlistId);
                return;
            }

            var tracks = await metadataService.GetPlaylistTracksAsync(provider, externalId);
            if (tracks == null || tracks.Count == 0)
            {
                _logger.LogWarning("No tracks found in playlist {PlaylistId}", playlistId);
                return;
            }

            _logger.LogInformation("Found {TrackCount} tracks in playlist '{PlaylistName}'", tracks.Count, playlist.Name);

            var downloadService = GetDownloadServiceForProvider(provider);

            if (downloadService == null)
            {
                _logger.LogError("No download service found for provider '{Provider}'", provider);
                return;
            }

            var downloadedTracks = new List<(Song Song, string LocalPath)>();

            foreach (var track in tracks)
            {
                try
                {
                    if (string.IsNullOrEmpty(track.ExternalId))
                    {
                        _logger.LogWarning("Track has no external ID, skipping: {Title}", track.Title);
                        continue;
                    }

                    _logger.LogInformation("Downloading track '{Artist} - {Title}'", track.Artist, track.Title);
                    var localPath = await downloadService.DownloadSongAsync(provider, track.ExternalId, cancellationToken);

                    downloadedTracks.Add((track, localPath));
                    _logger.LogDebug("Downloaded: {Path}", localPath);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download track '{Artist} - {Title}'", track.Artist, track.Title);
                }
            }

            if (downloadedTracks.Count == 0)
            {
                _logger.LogWarning("No tracks were successfully downloaded for playlist '{PlaylistName}'", playlist.Name);
                return;
            }

            // Write once so partial downloads cannot expose a half-updated playlist.
            await CreateM3UPlaylistAsync(playlist.Name, downloadedTracks);

            _logger.LogInformation("Playlist download completed: {DownloadedCount}/{TotalCount} tracks for '{PlaylistName}'",
                downloadedTracks.Count, tracks.Count, playlist.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download playlist {PlaylistId}", playlistId);
            throw;
        }
    }

    private async Task CreateM3UPlaylistAsync(string playlistName, List<(Song Song, string LocalPath)> tracks)
    {
        try
        {
            var fileName = PathHelper.SanitizeFileName(playlistName) + ".m3u";
            var playlistPath = Path.Combine(_playlistDirectory, fileName);

            var m3uContent = new StringBuilder();
            m3uContent.AppendLine("#EXTM3U");

            foreach (var (song, localPath) in tracks)
            {
                var relativePath = Path.GetRelativePath(_playlistDirectory, localPath);

                // Convert backslashes to forward slashes for M3U compatibility
                relativePath = relativePath.Replace('\\', '/');

                var duration = song.Duration ?? 0;
                m3uContent.AppendLine($"#EXTINF:{duration},{song.Artist} - {song.Title}");
                m3uContent.AppendLine(relativePath);
            }

            await IOFile.WriteAllTextAsync(playlistPath, m3uContent.ToString());
            _logger.LogDebug("Created M3U playlist: {Path}", playlistPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create M3U playlist for '{PlaylistName}'", playlistName);
            throw;
        }
    }

    public async Task AddTrackToM3UAsync(string playlistId, Song track, string localPath, bool isFullPlaylistDownload = false)
    {
        // Full downloads publish one complete M3U after every track finishes.
        if (isFullPlaylistDownload)
        {
            _logger.LogWarning("Skipping M3U update for track {TrackId} (full playlist download in progress)", track.Id);
            return;
        }

        try
        {
            if (!PlaylistIdHelper.IsExternalPlaylist(playlistId))
            {
                _logger.LogWarning("Invalid playlist ID format: {PlaylistId}", playlistId);
                return;
            }

            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

            var metadataService = GetMetadataServiceForProvider(provider);
            if (metadataService == null)
            {
                _logger.LogWarning("No metadata service found for provider '{Provider}'", provider);
                return;
            }

            var playlist = await metadataService.GetPlaylistAsync(provider, externalId);
            if (playlist == null)
            {
                _logger.LogWarning("Playlist not found: {PlaylistId}", playlistId);
                return;
            }

            // Rebuild from provider order; download completion order is nondeterministic.
            var allPlaylistTracks = await metadataService.GetPlaylistTracksAsync(provider, externalId);
            if (allPlaylistTracks == null || allPlaylistTracks.Count == 0)
            {
                _logger.LogWarning("No tracks found in playlist: {PlaylistId}", playlistId);
                return;
            }

            var fileName = PathHelper.SanitizeFileName(playlist.Name) + ".m3u";
            var playlistPath = Path.Combine(_playlistDirectory, fileName);

            var m3uContent = new StringBuilder();
            m3uContent.AppendLine("#EXTM3U");

            int addedCount = 0;
            foreach (var playlistTrack in allPlaylistTracks)
            {
                string? trackLocalPath = null;

                if (playlistTrack.Id == track.Id)
                {
                    trackLocalPath = localPath;
                }
                else
                {
                    var trackProvider = playlistTrack.ExternalProvider;
                    var trackExternalId = playlistTrack.ExternalId;

                    if (!string.IsNullOrEmpty(trackProvider) && !string.IsNullOrEmpty(trackExternalId))
                    {
                        var downloadService = GetDownloadServiceForProvider(trackProvider);

                        if (downloadService != null)
                        {
                            trackLocalPath = await downloadService.GetLocalPathIfExistsAsync(trackProvider, trackExternalId);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(trackLocalPath) && IOFile.Exists(trackLocalPath))
                {
                    var relativePath = Path.GetRelativePath(_playlistDirectory, trackLocalPath);
                    relativePath = relativePath.Replace('\\', '/');

                    var duration = playlistTrack.Duration ?? 0;
                    m3uContent.AppendLine($"#EXTINF:{duration},{playlistTrack.Artist} - {playlistTrack.Title}");
                    m3uContent.AppendLine(relativePath);
                    addedCount++;
                }
            }

            await IOFile.WriteAllTextAsync(playlistPath, m3uContent.ToString());
            _logger.LogDebug("Updated M3U playlist '{PlaylistName}' with {Count} tracks (in correct order)",
                playlist.Name, addedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add track to M3U playlist");
        }
    }

}
