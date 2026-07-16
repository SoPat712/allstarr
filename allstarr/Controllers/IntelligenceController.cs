using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/intelligence")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class IntelligenceController(
    IDbContextFactory<AllstarrDbContext> factory,
    IIntelligencePolicyService policies,
    IRecommendationRunService runs,
    ISmartPlaylistService smartPlaylists,
    IRecommendationProviderStatusService readiness,
    IEnumerable<IRecommendationProvider> providers) : ControllerBase
{
    private static readonly string[] SignalCatalog = ["play", "skip", "complete", "favorite", "playlist"];
    private readonly IReadOnlyDictionary<string, IRecommendationProvider> _providers = providers.ToDictionary(item => item.Id, StringComparer.Ordinal);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] IntelligenceScopeRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return Ok(State("unauthorized", scope, "This backend or library is not linked to your user."));
        try
        {
            var policy = await policies.GetAsync(scope, cancellationToken);
            var enabledProviders = ParseArray(policy?.EnabledProvidersJson);
            var enabledSignals = ParseArray(policy?.AllowedSignalTypesJson);
            var latestRun = await db.RecommendationRuns.AsNoTracking().Where(item => item.TenantId == scope.TenantId &&
                item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
                item.BackendInstanceId == scope.BackendInstanceId && item.LibraryScopeId == scope.LibraryScopeId)
                .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            var candidates = latestRun == null ? [] : await db.RecommendationCandidates.AsNoTracking()
                .Where(item => item.RunId == latestRun.Id && item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId)
                .OrderBy(item => item.Position).Take(100).ToListAsync(cancellationToken);
            var sets = await db.GeneratedSets.AsNoTracking().Where(item => item.TenantId == scope.TenantId &&
                item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
                item.BackendInstanceId == scope.BackendInstanceId && item.LibraryScopeId == scope.LibraryScopeId)
                .OrderByDescending(item => item.CreatedAt).Take(50).ToListAsync(cancellationToken);
            var setIds = sets.Select(item => item.Id).ToArray();
            var setCounts = await db.GeneratedSetEntries.AsNoTracking().Where(item => setIds.Contains(item.GeneratedSetId) &&
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId)
                .GroupBy(item => item.GeneratedSetId).Select(group => new { Id = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
            var profile = await db.ListeningProfiles.AsNoTracking().Where(item => item.TenantId == scope.TenantId &&
                item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
                item.BackendInstanceId == scope.BackendInstanceId && item.LibraryScopeId == scope.LibraryScopeId)
                .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            var providerIds = _providers.Keys.Union(enabledProviders).Order().ToArray();
            var providerReadiness = await readiness.ListAsync(scope, cancellationToken);
            var readinessById = providerReadiness.ToDictionary(item => item.ProviderId, StringComparer.Ordinal);
            var missingProvider = enabledProviders.Any(id => !readinessById.TryGetValue(id, out var item) || item.State != RecommendationProviderReadinessState.Ready);
            var state = policy == null ? "empty" : !policy.Enabled ? "disabled" :
                latestRun?.State == RecommendationRunState.Failed || missingProvider ? "degraded" : "configured";
            return Ok(new
            {
                state,
                scope = PublicScope(scope),
                message = latestRun?.ErrorCode,
                policy = policy == null ? null : new
                {
                    policy.Enabled,
                    policy.RetentionDays,
                    policy.Revision,
                    policy.TargetCredentialReferenceId,
                    targetCredentialConfigured = policy.TargetCredentialReferenceId.HasValue
                },
                availableSignalTypes = SignalCatalog.Select(id => new { id, label = Label(id), enabled = enabledSignals.Contains(id) }),
                providers = providerIds.Select(id =>
                {
                    var status = readinessById.TryGetValue(id, out var found) ? found : new RecommendationProviderReadiness(id, RecommendationProviderReadinessState.Unsupported, "provider_not_registered"); var available = status.State == RecommendationProviderReadinessState.Ready;
                    return new
                    {
                        id,
                        label = Label(id),
                        description = SourceDescription(id),
                        enabled = enabledProviders.Contains(id),
                        available,
                        state = ReadinessState(status.State),
                        reasonCode = status.SafeReasonCode
                    };
                }),
                actions = new
                {
                    canRun = policy?.Enabled == true && enabledProviders.Any(id => readinessById.TryGetValue(id, out var item) && item.State == RecommendationProviderReadinessState.Ready),
                    canGenerate = latestRun?.State == RecommendationRunState.Succeeded,
                    latestRunId = latestRun?.Id
                },
                candidates = candidates.Select(item => new
                {
                    item.TrackKey,
                    item.Score,
                    item.Source,
                    explanations = ParseSignals(item.SignalsJson)
                }),
                generatedSets = sets.Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.CreatedAt,
                    trackCount = setCounts.GetValueOrDefault(item.Id),
                    state = item.MaterializationState.ToString().ToLowerInvariant(),
                    item.BackendPlaylistId,
                    item.TargetRevision,
                    errorCode = item.LastErrorCode,
                    materialized = item.MaterializationState == GeneratedSetMaterializationState.Succeeded
                }),
                visualization = ProfileValues(profile?.ProfileJson)
            });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Ok(State("error", scope, "Stored intelligence data could not be read safely."));
        }
    }

    [HttpPut("policy")]
    public async Task<IActionResult> SetPolicy([FromBody] IntelligencePolicyRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        if (request.EnabledProviders.Any(id => !_providers.ContainsKey(id.Trim().ToLowerInvariant())))
            return BadRequest(new { error = "recommendation_provider_unavailable" });
        try
        {
            var readinessValues = await readiness.ListAsync(scope, cancellationToken);
            var requestedReadiness = request.EnabledProviders.Select(id => readinessValues.SingleOrDefault(item => item.ProviderId == id.Trim().ToLowerInvariant()) ??
                new RecommendationProviderReadiness(id, RecommendationProviderReadinessState.Unsupported, "provider_not_registered")).ToArray();
            if (requestedReadiness.Any(item => item.State != RecommendationProviderReadinessState.Ready))
                return Conflict(new
                {
                    error = "recommendation_provider_not_ready",
                    providers = requestedReadiness.Select(item => new { id = item.ProviderId, state = ReadinessState(item.State), reasonCode = item.SafeReasonCode })
                });
            var current = await policies.GetAsync(scope, cancellationToken);
            if (current != null && request.ExpectedRevision != current.Revision)
                return Conflict(new { error = "intelligence_policy_revision_conflict" });
            var policy = await policies.SetAsync(scope, new(request.Enabled, request.RetentionDays,
                request.AllowedSignalTypes, request.EnabledProviders, request.TargetCredentialReferenceId), cancellationToken);
            return Ok(new { policy.Id, policy.Enabled, policy.RetentionDays, policy.Revision });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = "intelligence_policy_invalid", message = exception.Message }); }
        catch (UnauthorizedAccessException) { return NotFound(); }
    }

    [HttpDelete("data")]
    public async Task<IActionResult> DisableAndPurge([FromBody] IntelligenceScopeRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await policies.DisableAndPurgeAsync(scope, cancellationToken); return NoContent();
    }

    [HttpPost("runs")]
    public async Task<IActionResult> Enqueue([FromBody] IntelligenceRunRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        try
        {
            var receipt = await runs.EnqueueAsync(scope, request.SeedTrackKeys, request.Limit,
                request.IdempotencyKey, cancellationToken);
            return Accepted(new { receipt.RunId, receipt.JobId, receipt.Created, state = receipt.State.ToString().ToLowerInvariant() });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = "recommendation_request_invalid", message = exception.Message }); }
        catch (InvalidOperationException) { return Conflict(new { error = "intelligence_not_ready" }); }
    }

    [HttpPost("generated-sets")]
    public async Task<IActionResult> GenerateSet([FromBody] IntelligenceGeneratedSetRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.RecommendationCandidates.AsNoTracking().Where(item => item.RunId == request.RunId &&
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId).OrderBy(item => item.Position)
            .Select(item => new { item.TrackKey, item.Score, item.Source, item.SignalsJson, item.IdentityJson }).ToListAsync(cancellationToken);
        try
        {
            var id = await smartPlaylists.CreateGeneratedSetAsync(scope, request.RunId, request.Name,
                candidates.Select(item => new RecommendationCandidate(item.TrackKey, item.Score, item.Source,
                    ParseSignals(item.SignalsJson), ParseIdentity(item.IdentityJson))).ToArray(), cancellationToken);
            return Ok(new { id, state = "preview" });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = "generated_playlist_invalid", message = exception.Message }); }
        catch (UnauthorizedAccessException) { return NotFound(); }
    }

    private bool TrySessionScope(IntelligenceScopeRequest request, out IntelligenceScope scope, out IActionResult? error)
    {
        scope = null!; error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession session)
        { error = Unauthorized(new { error = "Authentication required" }); return false; }
        if (session.TenantId is not { } tenant || session.AllstarrUserId is not { } user)
        { error = StatusCode(403, new { error = "linked_user_required" }); return false; }
        try
        {
            var protocol = request.Protocol?.Trim().ToLowerInvariant() ?? "";
            var backend = request.BackendInstanceId?.Trim() ?? ""; var library = request.LibraryScopeId?.Trim() ?? "";
            scope = new(tenant, user, protocol, backend, library); IntelligencePolicyService.ValidateScope(scope); return true;
        }
        catch (ArgumentException) { error = BadRequest(new { error = "intelligence_scope_invalid" }); return false; }
    }
    private static Task<bool> OwnsBackend(AllstarrDbContext db, IntelligenceScope scope, CancellationToken token) =>
        db.BackendIdentities.AsNoTracking().AnyAsync(item => item.TenantId == scope.TenantId && item.UserId == scope.OwnerUserId &&
            item.BackendType == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId, token);
    private static HashSet<string> ParseArray(string? json) => (JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []).ToHashSet(StringComparer.Ordinal);
    private static IReadOnlyList<RecommendationSignal> ParseSignals(string json) => JsonSerializer.Deserialize<RecommendationSignal[]>(json) ?? [];
    private static RecommendationTrackIdentity? ParseIdentity(string json) =>
        JsonSerializer.Deserialize<RecommendationTrackIdentity>(json);
    private static object[] ProfileValues(string? json)
    {
        if (json == null) return [];
        var value = JsonSerializer.Deserialize<ListeningProfile>(json); if (value == null) return [];
        var total = Math.Max(1, value.PlayCount + value.SkipCount + value.FavoriteCount);
        return [new { key = "plays", label = "Plays", value = (double)value.PlayCount / total },
            new { key = "skips", label = "Skips", value = (double)value.SkipCount / total },
            new { key = "favorites", label = "Favorites", value = (double)value.FavoriteCount / total }];
    }
    private static object State(string state, IntelligenceScope scope, string message) => new
    {
        state,
        scope = PublicScope(scope),
        message,
        availableSignalTypes = Array.Empty<object>(),
        providers = Array.Empty<object>(),
        candidates = Array.Empty<object>(),
        generatedSets = Array.Empty<object>(),
        visualization = Array.Empty<object>(),
        actions = new { canRun = false, canGenerate = false }
    };
    private static object PublicScope(IntelligenceScope scope) => new { scope.Protocol, scope.BackendInstanceId, scope.LibraryScopeId };
    private static string Label(string value) => string.Join(' ', value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
        .Select(item => char.ToUpperInvariant(item[0]) + item[1..]));
    private static string SourceDescription(string id) => id switch
    {
        "lastfm" => "Personalized from your opted-in Last.fm listening context.",
        "audiomuse-ai" => "Personalized sonic similarity from the optional AudioMuse-AI sidecar.",
        "musicbrainz-local" => "Local similarity using MusicBrainz-enriched genres, credits, and relationships. MusicBrainz is metadata, not a personalized recommendation account.",
        "jellyfin-instant-mix" => "Your linked Jellyfin library's Instant Mix results.",
        "listenbrainz" => "Personalized from your opted-in ListenBrainz listening context.",
        "local-rules" => "Private rules over retained local listening and library signals.",
        _ => "Registered recommendation source."
    };
    private static string ReadinessState(RecommendationProviderReadinessState state) => state switch
    {
        RecommendationProviderReadinessState.Ready => "ready",
        RecommendationProviderReadinessState.Degraded => "degraded",
        RecommendationProviderReadinessState.Unauthorized => "unauthorized",
        RecommendationProviderReadinessState.Disabled => "disabled",
        _ => "unconfigured"
    };
}

public class IntelligenceScopeRequest { public string Protocol { get; set; } = ""; public string BackendInstanceId { get; set; } = ""; public string LibraryScopeId { get; set; } = ""; }
public sealed class IntelligencePolicyRequest : IntelligenceScopeRequest { public bool Enabled { get; set; } public int RetentionDays { get; set; } = 30; public List<string> AllowedSignalTypes { get; set; } = []; public List<string> EnabledProviders { get; set; } = []; public Guid? TargetCredentialReferenceId { get; set; } public long ExpectedRevision { get; set; } }
public sealed class IntelligenceRunRequest : IntelligenceScopeRequest { public List<string> SeedTrackKeys { get; set; } = []; public int Limit { get; set; } = 25; public string IdempotencyKey { get; set; } = ""; }
public sealed class IntelligenceGeneratedSetRequest : IntelligenceScopeRequest { public Guid RunId { get; set; } public string Name { get; set; } = ""; }
