using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    [HttpPost("audiomuse/fingerprint")]
    public async Task<IActionResult> GetAudioMuseFingerprint(
        [FromBody] AudioMuseFingerprintRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        if (request.PeriodDays is not (30 or 90 or 365) || request.Limit is < 1 or > 200)
            return BadRequest(new { error = "audiomuse_request_invalid" });

        return await AudioMuseResult(async () =>
        {
            var now = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
            var from = now.AddDays(-request.PeriodDays);
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var history = db.ListeningEvents.AsNoTracking().Where(item =>
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                item.LibraryScopeId == scope.LibraryScopeId && item.State == ListeningEventState.Completed &&
                item.ListenedAt >= from && item.ListenedAt <= now);
            var completedListens = await history.CountAsync(cancellationToken);
            var summaries = await history.Where(item =>
                    item.LibraryTrackId != null || item.SourceKind == "protocol")
                .GroupBy(item => new { item.LibraryTrackId, item.TrackReference, item.Album })
                .Select(group => new
                {
                    group.Key.LibraryTrackId,
                    group.Key.TrackReference,
                    group.Key.Album,
                    Plays = group.Count(),
                    LastPlayed = group.Max(item => item.ListenedAt)!.Value
                })
                .OrderByDescending(item => item.Plays).ThenByDescending(item => item.LastPlayed)
                .Take(100).ToListAsync(cancellationToken);
            var albumCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seeds = new List<string>(20);
            foreach (var item in summaries.OrderByDescending(item => item.Plays *
                         Math.Pow(.5, Math.Max(0, (now - item.LastPlayed).TotalDays) / 30d))
                         .ThenBy(item => item.TrackReference, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(item.Album) &&
                    albumCounts.GetValueOrDefault(item.Album) >= 3) continue;
                var key = item.LibraryTrackId is { } id ? $"library:{id:N}" : item.TrackReference;
                if (!seeds.Contains(key, StringComparer.Ordinal)) seeds.Add(key);
                if (!string.IsNullOrWhiteSpace(item.Album))
                    albumCounts[item.Album] = albumCounts.GetValueOrDefault(item.Album) + 1;
                if (seeds.Count == 20) break;
            }

            var profile = new ListeningProfile(scope.TenantId, scope.OwnerUserId,
                scope.BackendInstanceId, scope.LibraryScopeId, completedListens, 0, 0,
                new Dictionary<string, double>(), from, now)
            { TopTrackKeys = seeds };
            var tracks = await _audioMuse.RecommendAsync(
                new(scope, profile, seeds, request.Limit), cancellationToken);
            return new
            {
                tracks = tracks.Select(TrackDto),
                request.PeriodDays,
                completedListens,
                seedCount = seeds.Count
            };
        });
    }

    [HttpPost("audiomuse/generated-sets")]
    public async Task<IActionResult> CreateAudioMuseGeneratedSet(
        [FromBody] AudioMuseGeneratedSetRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!await OwnsAudioMuseScope(scope, cancellationToken)) return NotFound();
        if (request.TrackIds.Count is < 1 or > 200 || request.TrackIds.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Length > 500 || item != item.Trim() || item.Any(char.IsControl)) ||
            request.TrackIds.Distinct(StringComparer.Ordinal).Count() != request.TrackIds.Count)
            return BadRequest(new { error = "audiomuse_request_invalid" });
        var policy = await policies.GetAsync(scope, cancellationToken);
        if (policy?.Enabled != true || !ParseArray(policy.EnabledProvidersJson).Contains("audiomuse-ai"))
            return Conflict(new { error = "audiomuse_not_selected" });

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var local = await db.LibraryTracks.AsNoTracking().Where(item =>
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                item.LibraryScopeId == scope.LibraryScopeId && request.TrackIds.Contains(item.BackendItemId))
            .ToDictionaryAsync(item => item.BackendItemId, StringComparer.Ordinal, cancellationToken);
        if (local.Count != request.TrackIds.Count)
            return Conflict(new { error = "audiomuse_preview_stale" });
        var candidates = request.TrackIds.Select(trackId =>
        {
            var track = local[trackId];
            return new RecommendationCandidate(trackId, 1, "audiomuse-ai",
                [new("audiomuse-preview", 1, "Selected from your sound discovery preview.")],
                new("audiomuse-ai", Title: track.Title, Artist: track.Artist, Album: track.Album,
                    LibraryTrackId: track.Id, BackendItemId: track.BackendItemId))
            { CanonicalRecordingId = track.CanonicalRecordingId, SourceRevision = "audiomuse-preview-v1" };
        }).ToArray();
        try
        {
            var id = await smartPlaylists.CreateGeneratedSetAsync(scope, request.Name, candidates,
                request.IdempotencyKey, cancellationToken);
            return Accepted(new { id, state = "creating" });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = "generated_playlist_invalid", message = exception.Message });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { error = "intelligence_not_ready" });
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
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
        var offset = 0;
        if (request.Limit is < 1 or > 25 ||
            request.Cursor != null && (!int.TryParse(request.Cursor, out offset) || offset is < 0 or > 99))
            return BadRequest(new { error = "audiomuse_request_invalid" });
        return await AudioMuseResult(async () =>
        {
            var fetched = await _audioMuse.GetClustersAsync(scope,
                Math.Min(100, offset + request.Limit + 1), cancellationToken);
            return new
            {
                clusters = fetched.Skip(offset).Take(request.Limit)
                    .Select(item => new { item.Id, item.Name, tracks = item.Tracks.Select(TrackDto) }),
                nextCursor = fetched.Count > offset + request.Limit
                    ? (offset + request.Limit).ToString() : null
            };
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

public sealed class AudioMuseFingerprintRequest : IntelligenceScopeRequest
{
    public int PeriodDays { get; set; } = 90;
    public int Limit { get; set; } = 25;
}

public sealed class AudioMuseGeneratedSetRequest : IntelligenceScopeRequest
{
    public string Name { get; set; } = "";
    public List<string> TrackIds { get; set; } = [];
    public string IdempotencyKey { get; set; } = "";
}

public sealed class AudioMuseSearchRequest : IntelligenceScopeRequest
{
    public string Query { get; set; } = "";
    public string Mode { get; set; } = "text";
    public int Limit { get; set; } = 25;
}

public sealed class AudioMusePageRequest : IntelligenceScopeRequest
{
    public int Limit { get; set; } = 25;
    public string? Cursor { get; set; }
}
