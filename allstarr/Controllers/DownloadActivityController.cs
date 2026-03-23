using System.Text.Json;
using allstarr.Models.Download;
using allstarr.Services;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/downloads")]
public class DownloadActivityController : ControllerBase
{
    private readonly IEnumerable<IDownloadService> _downloadServices;
    private readonly ILogger<DownloadActivityController> _logger;

    public DownloadActivityController(
        IEnumerable<IDownloadService> downloadServices,
        ILogger<DownloadActivityController> logger)
    {
        _downloadServices = downloadServices;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current download queue as JSON.
    /// </summary>
    [HttpGet("queue")]
    public IActionResult GetDownloadQueue()
    {
        var allDownloads = GetAllActiveDownloads();
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
                var allDownloads = GetAllActiveDownloads();

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

    private List<DownloadInfo> GetAllActiveDownloads()
    {
        var allDownloads = new List<DownloadInfo>();
        foreach (var service in _downloadServices)
        {
            allDownloads.AddRange(service.GetActiveDownloads());
        }

        // Sort: InProgress first, then by StartedAt descending
        return allDownloads
            .OrderByDescending(d => d.Status == DownloadStatus.InProgress)
            .ThenByDescending(d => d.StartedAt)
            .ToList();
    }
}
