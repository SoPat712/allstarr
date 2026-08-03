using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public sealed partial class IntelligenceController
{
    [HttpPost("audiomuse/analysis")]
    public async Task<IActionResult> StartAudioMuseAnalysis(
        [FromBody] AudioMuseAnalysisRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        return await AudioMuseResult(() => _audioMuse.StartAnalysisAsync(
            scope, request.Rebuild, request.IdempotencyKey, cancellationToken), AnalysisDto);
    }

    [HttpGet("audiomuse/analysis/{jobId}")]
    public async Task<IActionResult> GetAudioMuseAnalysis(
        string jobId, [FromQuery] IntelligenceScopeRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        return await AudioMuseResult(() => _audioMuse.GetAnalysisProgressAsync(
            scope, jobId, cancellationToken), AnalysisDto);
    }

    [HttpPost("audiomuse/similar")]
    public async Task<IActionResult> GetAudioMuseSimilar(
        [FromBody] AudioMuseSimilarRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        return await AudioMuseResult(async () => new
        {
            tracks = (await _audioMuse.FindSimilarAsync(
                scope, request.SeedTrackIds, request.Limit, cancellationToken)).Select(TrackDto)
        });
    }

    [HttpPost("audiomuse/path")]
    public async Task<IActionResult> GetAudioMusePath(
        [FromBody] AudioMusePathRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        return await AudioMuseResult(async () =>
        {
            var path = await _audioMuse.FindPathAsync(scope, request.StartTrackId,
                request.EndTrackId, request.Limit, cancellationToken);
            return new { tracks = path.Tracks.Select(TrackDto), path.TotalDistance };
        });
    }

    [HttpPost("audiomuse/blend")]
    public async Task<IActionResult> GetAudioMuseBlend(
        [FromBody] AudioMuseBlendRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        return await AudioMuseResult(async () => new
        {
            tracks = (await _audioMuse.BlendAsync(scope, request.IncludeTrackIds,
                request.AvoidTrackIds, request.Limit, cancellationToken)).Select(TrackDto)
        });
    }

    [HttpPost("audiomuse/search")]
    public async Task<IActionResult> SearchAudioMuse(
        [FromBody] AudioMuseSearchRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        var mode = (request.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("text" or "lyrics"))
            return BadRequest(new { error = "audiomuse_search_mode_invalid" });
        return await AudioMuseResult(async () => new
        {
            tracks = (await _audioMuse.SearchAsync(scope, request.Query,
                mode == "lyrics", request.Limit, cancellationToken)).Select(TrackDto),
            mode
        });
    }

    [HttpGet("audiomuse/clusters")]
    public async Task<IActionResult> GetAudioMuseClusters(
        [FromQuery] AudioMusePageRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        return await AudioMuseResult(async () => new
        {
            clusters = (await _audioMuse.GetClustersAsync(scope, request.Limit, cancellationToken))
                .Select(item => new { item.Id, item.Name, tracks = item.Tracks.Select(TrackDto) })
        });
    }

    [HttpGet("audiomuse/map")]
    public async Task<IActionResult> GetAudioMuseMap(
        [FromQuery] AudioMusePageRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        return await AudioMuseResult(async () =>
        {
            var page = await _audioMuse.GetMapAsync(scope,
                new ProviderPageRequest(request.Limit, request.Cursor), cancellationToken);
            return new
            {
                items = page.Items.Select(item => new
                {
                    trackId = item.Identity.BackendItemId,
                    item.Identity.Title,
                    item.Identity.Artist,
                    item.Identity.Album,
                    item.Identity.LibraryTrackId,
                    item.X,
                    item.Y,
                    item.ClusterId
                }),
                page.Projection,
                page.NextCursor,
                page.IsPartial,
                page.SnapshotVersion
            };
        });
    }

    private async Task<bool> OwnsAudioMuseScope(IntelligenceScope scope, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await OwnsBackend(db, scope, cancellationToken);
    }

    private async Task<IActionResult> AudioMuseResult<T>(Func<Task<T>> operation,
        Func<T, object>? map = null)
    {
        try
        {
            var value = await operation();
            return Ok(map == null ? value : map(value));
        }
        catch (ArgumentException) { return BadRequest(new { error = "audiomuse_request_invalid" }); }
        catch (NotSupportedException) { return Conflict(new { error = "audiomuse_operation_unavailable" }); }
        catch (UnauthorizedAccessException) { return Conflict(new { error = "audiomuse_reconnect_or_scope_required" }); }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "audiomuse_temporarily_unavailable" });
        }
    }

    private static object AnalysisDto(ProviderAnalysisProgress value) => new
    {
        value.JobId,
        state = value.State.ToString().ToLowerInvariant(),
        value.Completed,
        value.Total,
        value.SafeCode
    };

    private static object TrackDto(RecommendationSourceItem item) => new
    {
        trackId = item.Identity?.BackendItemId,
        item.Identity?.Title,
        item.Identity?.Artist,
        item.Identity?.Album,
        item.Identity?.LibraryTrackId,
        item.Score,
        explanation = item.Signals.FirstOrDefault()?.Explanation
    };
}

public sealed class AudioMuseAnalysisRequest : IntelligenceScopeRequest
{
    public bool Rebuild { get; set; }
    public string IdempotencyKey { get; set; } = "";
}

public sealed class AudioMuseSimilarRequest : IntelligenceScopeRequest
{
    public List<string> SeedTrackIds { get; set; } = [];
    public int Limit { get; set; } = 25;
}

public sealed class AudioMusePathRequest : IntelligenceScopeRequest
{
    public string StartTrackId { get; set; } = "";
    public string EndTrackId { get; set; } = "";
    public int Limit { get; set; } = 25;
}

public sealed class AudioMuseBlendRequest : IntelligenceScopeRequest
{
    public List<string> IncludeTrackIds { get; set; } = [];
    public List<string> AvoidTrackIds { get; set; } = [];
    public int Limit { get; set; } = 25;
}

public sealed class AudioMuseSearchRequest : IntelligenceScopeRequest
{
    public string Query { get; set; } = "";
    public string Mode { get; set; } = "text";
    public int Limit { get; set; } = 25;
}

public sealed class AudioMusePageRequest : IntelligenceScopeRequest
{
    public int Limit { get; set; } = 50;
    public string? Cursor { get; set; }
}
