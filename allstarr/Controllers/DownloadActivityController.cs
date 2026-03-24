using System.Text.Json;
using allstarr.Models.Download;
using allstarr.Services;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/downloads")]
public class DownloadActivityController : ControllerBase
{
    private readonly IEnumerable<IDownloadService> _downloadServices;
    private readonly JellyfinSessionManager _sessionManager;
    private readonly ILogger<DownloadActivityController> _logger;

    public DownloadActivityController(
        IEnumerable<IDownloadService> downloadServices,
        JellyfinSessionManager sessionManager,
        ILogger<DownloadActivityController> logger)
    {
        _downloadServices = downloadServices;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current download queue as JSON.
    /// </summary>
    [HttpGet("queue")]
    public IActionResult GetDownloadQueue()
    {
        var allDownloads = GetAllActivityEntries();
        return Ok(allDownloads);
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
                var allDownloads = GetAllActivityEntries();

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

    private List<DownloadActivityEntry> GetAllActivityEntries()
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

        var playbackByItemId = _sessionManager
            .GetActivePlaybackStates(TimeSpan.FromMinutes(5))
            .GroupBy(state => NormalizeExternalItemId(state.ItemId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(state => state.LastActivity).First());

        return orderedDownloads
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
                    DurationSeconds = download.DurationSeconds,
                    LocalPath = download.LocalPath,
                    ErrorMessage = download.ErrorMessage,
                    StartedAt = download.StartedAt,
                    CompletedAt = download.CompletedAt,
                    IsPlaying = hasPlayback,
                    PlaybackPositionSeconds = hasPlayback
                        ? (int)Math.Max(0, playbackState!.PositionTicks / TimeSpan.TicksPerSecond)
                        : null,
                    PlaybackProgress = playbackProgress
                };
            })
            .ToList();
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

    private sealed class DownloadActivityEntry : DownloadInfo
    {
        public bool IsPlaying { get; init; }
        public int? PlaybackPositionSeconds { get; init; }
        public double? PlaybackProgress { get; init; }
    }
}
