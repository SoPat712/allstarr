using System.Text.Json;
using allstarr.Models.Download;
using allstarr.Services;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using allstarr.Filters;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/downloads")]
[ServiceFilter(typeof(AdminPortFilter))]
public class DownloadActivityController : ControllerBase
{
    private readonly IEnumerable<IDownloadService> _downloadServices;
    private readonly IReadOnlyList<IPlaybackActivitySource> _playbackSources;
    private readonly IReadOnlyList<IPlaybackMetadataResolver> _metadataResolvers;
    private readonly ILogger<DownloadActivityController> _logger;
    private readonly IPlaybackDeliveryActivitySource? _playbackDeliveries;

    public DownloadActivityController(
        IEnumerable<IDownloadService> downloadServices,
        IEnumerable<IPlaybackActivitySource> playbackSources,
        IEnumerable<IPlaybackMetadataResolver> metadataResolvers,
        ILogger<DownloadActivityController> logger,
        IPlaybackDeliveryActivitySource? playbackDeliveries = null)
    {
        _downloadServices = downloadServices;
        _playbackSources = playbackSources.ToList();
        _metadataResolvers = metadataResolvers.ToList();
        _logger = logger;
        _playbackDeliveries = playbackDeliveries;
    }

    /// <summary>
    /// Returns the current download queue as JSON.
    /// </summary>
    [HttpGet("queue")]
    public async Task<IActionResult> GetDownloadQueue()
    {
        var allDownloads = await GetAllActivityEntriesAsync(HttpContext.RequestAborted);
        return Ok(allDownloads);
    }

    [HttpGet("artwork/{itemId}")]
    public async Task<IActionResult> GetPlaybackArtwork(
        string itemId,
        CancellationToken cancellationToken)
    {
        foreach (var resolver in _metadataResolvers)
        {
            var artwork = await resolver.ResolveArtworkAsync(itemId, cancellationToken);
            if (artwork != null)
            {
                return File(artwork.Content, artwork.ContentType);
            }
        }

        return NotFound();
    }

    /// <summary>
    /// Server-Sent Events (SSE) endpoint that pushes the download queue state
    /// in real-time.
    /// </summary>
    [HttpGet("activity")]
    public async Task GetDownloadActivity(CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        // Use the request aborted token or the provided cancellation token.
        var requestAborted = HttpContext.RequestAborted;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestAborted);
        var token = linkedCts.Token;

        _logger.LogInformation("Download activity SSE connection opened.");

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        try
        {
            while (!token.IsCancellationRequested)
            {
                var allDownloads = await GetAllActivityEntriesAsync(token);

                var payload = JsonSerializer.Serialize(allDownloads, jsonOptions);
                var message = $"data: {payload}\n\n";

                await Response.WriteAsync(message, token);
                await Response.Body.FlushAsync(token);

                await Task.Delay(1000, token); // Poll every 1 second
            }
        }
        catch (TaskCanceledException)
        {
            // Client gracefully disconnected or requested cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while pushing download activity stream.");
        }
        finally
        {
            _logger.LogInformation("Download activity SSE connection closed.");
        }
    }

    private async Task<List<DownloadActivityEntry>> GetAllActivityEntriesAsync(CancellationToken cancellationToken)
    {
        var allDownloads = new List<DownloadInfo>();
        foreach (var service in _downloadServices)
        {
            allDownloads.AddRange(service.GetActiveDownloads());
        }

        var orderedDownloads = allDownloads
            .OrderByDescending(d => d.Status == DownloadStatus.InProgress)
            .ThenByDescending(d => d.StartedAt)
            .ToList();

        var playbackByItemId = _playbackSources
            .SelectMany(source => source.GetActivePlaybackStates(TimeSpan.FromMinutes(5)))
            .GroupBy(state => NormalizeExternalItemId(state.ItemId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(state => state.LastActivity).First());

        var entries = orderedDownloads
            .Select(download =>
            {
                var normalizedSongId = NormalizeExternalItemId(download.SongId);
                var hasPlayback = playbackByItemId.TryGetValue(normalizedSongId, out var playbackState);
                var playbackProgress = hasPlayback && download.DurationSeconds.GetValueOrDefault() > 0
                    ? Math.Clamp(
                        playbackState!.PositionTicks / (double)TimeSpan.TicksPerSecond / download.DurationSeconds!.Value,
                        0d,
                        1d)
                    : (double?)null;

                return new DownloadActivityEntry
                {
                    SongId = download.SongId,
                    ExternalId = download.ExternalId,
                    ExternalProvider = download.ExternalProvider,
                    Title = download.Title,
                    Artist = download.Artist,
                    Status = download.Status,
                    Progress = download.Progress,
                    RequestedForStreaming = download.RequestedForStreaming,
                    CoverArtUrl = download.CoverArtUrl,
                    DurationSeconds = download.DurationSeconds,
                    LocalPath = download.LocalPath,
                    ErrorMessage = download.ErrorMessage,
                    StartedAt = download.StartedAt,
                    CompletedAt = download.CompletedAt,
                    IsPlaying = hasPlayback,
                    PlaybackLastActivity = hasPlayback ? playbackState!.LastActivity : null,
                    PlaybackPositionSeconds = hasPlayback
                        ? (int)Math.Max(0, playbackState!.PositionTicks / TimeSpan.TicksPerSecond)
                        : null,
                    PlaybackProgress = playbackProgress,
                    Scrobbled = hasPlayback && _playbackDeliveries?.WasDelivered(
                        normalizedSongId,
                        playbackState!.DeviceId) == true
                };
            })
            .ToList();

        var knownIds = orderedDownloads
            .Select(download => NormalizeExternalItemId(download.SongId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (itemId, playbackState) in playbackByItemId)
        {
            if (string.IsNullOrWhiteSpace(itemId) || knownIds.Contains(itemId))
            {
                continue;
            }

            var playbackMetadata = await TryResolvePlaybackMetadataAsync(itemId, cancellationToken);

            entries.Add(new DownloadActivityEntry
            {
                SongId = itemId,
                ExternalId = itemId,
                ExternalProvider = ResolvePlaybackProvider(itemId),
                Title = playbackMetadata?.Title ?? ResolvePlaybackTitle(itemId),
                Artist = playbackMetadata?.Artist ?? "External provider",
                Status = DownloadStatus.Completed,
                Progress = 1,
                RequestedForStreaming = false,
                StartedAt = playbackState.LastActivity,
                IsPlaying = true,
                PlaybackLastActivity = playbackState.LastActivity,
                CoverArtUrl = playbackMetadata?.CoverArtUrl,
                DurationSeconds = playbackMetadata?.DurationSeconds,
                PlaybackPositionSeconds = (int)Math.Max(0, playbackState.PositionTicks / TimeSpan.TicksPerSecond),
                PlaybackProgress = playbackMetadata?.DurationSeconds > 0
                    ? Math.Clamp(
                        playbackState.PositionTicks / (double)TimeSpan.TicksPerSecond /
                        playbackMetadata.DurationSeconds.Value,
                        0d,
                        1d)
                    : null,
                Scrobbled = _playbackDeliveries?.WasDelivered(itemId, playbackState.DeviceId) == true
            });
        }

        return entries
            .OrderByDescending(entry => entry.IsPlaying)
            .ThenByDescending(entry => entry.Status == DownloadStatus.InProgress)
            .ThenByDescending(entry => entry.StartedAt)
            .ToList();
    }

    private async Task<PlaybackTrackMetadata?> TryResolvePlaybackMetadataAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        foreach (var resolver in _metadataResolvers)
        {
            try
            {
                var metadata = await resolver.ResolveAsync(itemId, cancellationToken);
                if (metadata != null)
                {
                    return metadata;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Playback metadata resolver failed for item {ItemId}", itemId);
            }
        }

        return null;
    }

    private static string NormalizeExternalItemId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return itemId;
        }

        var parts = itemId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return itemId;
        }

        var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "song",
            "album",
            "artist"
        };

        if (parts.Length >= 4 && knownTypes.Contains(parts[2]))
        {
            return itemId;
        }

        return $"ext-{parts[1]}-song-{string.Join("-", parts.Skip(2))}";
    }

    private static string ResolvePlaybackProvider(string itemId)
    {
        if (!itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return "jellyfin";
        }

        return ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId)?.Provider.ToLowerInvariant() ?? "external";
    }

    private static string ResolvePlaybackTitle(string itemId)
    {
        if (!itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return "Local Jellyfin track";
        }

        return ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId)?.ExternalId ?? "External track";
    }

    private sealed class DownloadActivityEntry : DownloadInfo
    {
        public bool IsPlaying { get; init; }
        public DateTime? PlaybackLastActivity { get; init; }
        public int? PlaybackPositionSeconds { get; init; }
        public double? PlaybackProgress { get; init; }
        public bool Scrobbled { get; init; }
    }
}
