using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/intelligence")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed partial class IntelligenceController(
    IDbContextFactory<AllstarrDbContext> factory,
    IIntelligencePolicyService policies,
    IRecommendationRunService runs,
    ISmartPlaylistService smartPlaylists,
    IRecommendationProviderStatusService readiness,
    IEnumerable<IRecommendationProvider> providers,
    IAudioMuseRecommendationClient audioMuse,
    IPlatformClock? clock = null,
    IEnumerable<IExactScopePlaybackScrobbleTarget>? scrobbleTargets = null) : ControllerBase
{
    private static readonly string[] SignalCatalog = ["play", "skip", "complete", "favorite", "playlist"];
    private readonly IDbContextFactory<AllstarrDbContext> _factory = factory;
    private readonly IPlatformClock? _clock = clock;
    private readonly IAudioMuseRecommendationClient _audioMuse = audioMuse;
    private readonly IReadOnlyDictionary<string, IRecommendationProvider> _providers = providers.ToDictionary(item => item.Id, StringComparer.Ordinal);
    private readonly IExactScopePlaybackScrobbleTarget[] _scrobbleTargets =
        scrobbleTargets?.GroupBy(item => item.ProviderId, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(item => item.ProviderId, StringComparer.Ordinal).ToArray() ?? [];

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] IntelligenceScopeRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
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
            var latestJob = latestRun == null ? null : await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == latestRun.JobId && item.TenantId == scope.TenantId &&
                item.OwnerUserId == scope.OwnerUserId && item.LibraryScopeId == scope.LibraryScopeId,
                cancellationToken);
            var latestProgress = latestJob == null ? null : await db.AuditEvents.AsNoTracking()
                .Where(item => item.Category == "job-progress" &&
                    item.CorrelationId == latestJob.CorrelationId)
                .OrderByDescending(item => item.CreatedAt).Select(item => item.DetailsJson)
                .FirstOrDefaultAsync(cancellationToken);
            var candidates = latestRun == null ? [] : await db.RecommendationCandidates.AsNoTracking()
                .Where(item => item.RunId == latestRun.Id && item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId)
                .OrderBy(item => item.Position).Take(100).ToListAsync(cancellationToken);
            var candidateIds = candidates.Select(item => item.Id).ToArray();
            var feedback = await db.RecommendationFeedback.AsNoTracking()
                .Where(item => candidateIds.Contains(item.CandidateId) && item.TenantId == scope.TenantId &&
                               item.OwnerUserId == scope.OwnerUserId)
                .ToDictionaryAsync(item => item.CandidateId, cancellationToken);
            var candidateIdentities = candidates.ToDictionary(item => item.Id, item => ParseIdentity(item.IdentityJson));
            var libraryTrackIds = candidateIdentities.Values.Select(item => item?.LibraryTrackId).OfType<Guid>().ToArray();
            var canonicalIds = candidates.Select(item => item.CanonicalRecordingId).OfType<Guid>().ToArray();
            var localTracks = await db.LibraryTracks.AsNoTracking().Where(item =>
                    item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                    item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                    item.LibraryScopeId == scope.LibraryScopeId &&
                    (libraryTrackIds.Contains(item.Id) ||
                     item.CanonicalRecordingId.HasValue && canonicalIds.Contains(item.CanonicalRecordingId.Value)))
                .ToListAsync(cancellationToken);
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
            var scheduleRecords = await db.JobSchedules.AsNoTracking().Where(item => item.TenantId == scope.TenantId &&
                item.OwnerUserId == scope.OwnerUserId && item.LibraryScopeId == scope.LibraryScopeId &&
                item.JobType == DurableScheduleEngine.RecommendationJobType).OrderBy(item => item.CreatedAt)
                .ToListAsync(cancellationToken);
            var schedules = scheduleRecords.Select(item => (Record: item, Template: TryParseScheduleTemplate(item.PayloadTemplateJson)))
                .Where(item => item.Template is { Version: 1 } && item.Template.IntelligencePolicyId == policy?.Id).ToArray();
            var providerIds = _providers.Keys.Union(enabledProviders).Order().ToArray();
            var providerReadiness = await readiness.ListAsync(scope, cancellationToken);
            var readinessById = providerReadiness.ToDictionary(item => item.ProviderId, StringComparer.Ordinal);
            var missingProvider = enabledProviders.Any(id => !readinessById.TryGetValue(id, out var item) || item.State != RecommendationProviderReadinessState.Ready);
            var scopedOccurrences = db.ListeningEvents.Where(item =>
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                item.LibraryScopeId == scope.LibraryScopeId);
            var enrichmentCounts = await scopedOccurrences.AsNoTracking()
                .GroupBy(item => item.MusicBrainzEnrichmentState)
                .Select(group => new { State = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.State, item => item.Count, cancellationToken);
            var occurrenceKeys = scopedOccurrences.Select(item => item.OccurrenceKey);
            var listeningServices = new List<object>(_scrobbleTargets.Length);
            foreach (var target in _scrobbleTargets)
            {
                var configured = await target.IsConfiguredAsync(scope, cancellationToken);
                var latest = await db.PlaybackDeliveryCheckpoints.AsNoTracking().Where(item =>
                        item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                        item.Kind == PlaybackScrobbleDeliveryKind.Completed && item.OccurrenceKey != null &&
                        occurrenceKeys.Contains(item.OccurrenceKey) && item.TargetId == target.ProviderId)
                    .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                listeningServices.Add(new
                {
                    id = target.ProviderId,
                    label = PlaybackTargetLabel(target.ProviderId),
                    configured,
                    latestState = latest?.State.ToString().ToLowerInvariant(),
                    latest?.RequiresReauthentication,
                    latest?.UpdatedAt
                });
            }
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
                listeningServices,
                songDetails = new
                {
                    pending = enrichmentCounts.GetValueOrDefault(MusicBrainzEnrichmentState.Pending),
                    resolved = enrichmentCounts.GetValueOrDefault(MusicBrainzEnrichmentState.Resolved),
                    unresolved = enrichmentCounts.GetValueOrDefault(MusicBrainzEnrichmentState.Unresolved),
                    failed = enrichmentCounts.GetValueOrDefault(MusicBrainzEnrichmentState.Failed)
                },
                actions = new
                {
                    canRun = policy?.Enabled == true && enabledProviders.Any(id => readinessById.TryGetValue(id, out var item) && item.State == RecommendationProviderReadinessState.Ready),
                    canGenerate = latestRun?.State == RecommendationRunState.Succeeded,
                    latestRunId = latestRun?.Id,
                    latestRunState = latestJob?.State.ToString().ToLowerInvariant() ??
                        latestRun?.State.ToString().ToLowerInvariant(),
                    latestJobId = latestJob?.Id,
                    latestJob?.AttemptCount,
                    latestJob?.FailureCount,
                    latestJob?.MaxAttempts,
                    canCancel = latestJob?.State is DurableJobState.Pending or DurableJobState.RetryScheduled
                        or DurableJobState.Running,
                    progress = latestProgress == null ? (JsonElement?)null :
                        JsonSerializer.Deserialize<JsonElement>(latestProgress)
                },
                candidates = candidates.Select(item =>
                {
                    var identity = candidateIdentities[item.Id];
                    var local = localTracks.FirstOrDefault(track => track.Id == identity?.LibraryTrackId) ??
                                localTracks.FirstOrDefault(track => track.CanonicalRecordingId == item.CanonicalRecordingId);
                    return new
                    {
                        id = item.Id,
                        item.TrackKey,
                        title = identity?.Title ?? local?.Title,
                        artist = identity?.Artist ?? local?.Artist,
                        album = identity?.Album ?? local?.Album,
                        artworkUrl = local?.CoverArtReference == null ? null :
                            $"/api/admin/downloads/artwork/{Uri.EscapeDataString(local.BackendItemId)}",
                        item.Score,
                        item.Source,
                        providerId = identity?.ProviderId ?? item.Source,
                        item.CanonicalRecordingId,
                        item.ProviderAccountId,
                        item.SourceRevision,
                        item.Revision,
                        explanations = ParseSignals(item.SignalsJson),
                        exclusions = ParseArray(item.ExclusionsJson),
                        feedback = feedback.TryGetValue(item.Id, out var value)
                        ? new { value.Kind, value.ReasonCode, value.UpdatedAt, value.Revision }
                        : null
                    };
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
                schedules = schedules.Select(item => ToScheduleDto(item.Record, item.Template!)),
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
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
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
    public async Task<IActionResult> DisableAndPurge(
        [FromBody] IntelligenceScopeRequest request,
        [FromServices] ListeningIntakeTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        foreach (var token in await tokens.ListAsync(scope, cancellationToken))
            await tokens.RevokeAsync(scope, token.Id, cancellationToken);
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
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.RecommendationCandidates.AsNoTracking().Where(item => item.RunId == request.RunId &&
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId).OrderBy(item => item.Position)
            .Select(item => new
            {
                item.TrackKey,
                item.Score,
                item.Source,
                item.SignalsJson,
                item.IdentityJson,
                item.CanonicalRecordingId,
                item.ProviderAccountId,
                item.SourceRevision,
                item.ExclusionsJson
            })
            .ToListAsync(cancellationToken);
        try
        {
            var id = await smartPlaylists.CreateGeneratedSetAsync(scope, request.RunId, request.Name,
                candidates.Where(item => ParseArray(item.ExclusionsJson).Count == 0)
                    .Select(item => new RecommendationCandidate(item.TrackKey, item.Score, item.Source,
                        ParseSignals(item.SignalsJson), ParseIdentity(item.IdentityJson))
                    {
                        CanonicalRecordingId = item.CanonicalRecordingId,
                        ProviderAccountId = item.ProviderAccountId,
                        SourceRevision = item.SourceRevision
                    }).ToArray(), cancellationToken);
            return Ok(new { id, state = "preview" });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = "generated_playlist_invalid", message = exception.Message }); }
        catch (UnauthorizedAccessException) { return NotFound(); }
    }

    [HttpPut("candidates/{candidateId:guid}/feedback")]
    public async Task<IActionResult> SetFeedback(Guid candidateId,
        [FromBody] IntelligenceFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        var kind = request.Kind.Trim().ToLowerInvariant();
        var reason = string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode.Trim().ToLowerInvariant();
        if (kind is not ("like" or "dislike" or "dismiss") || reason?.Length > 100 ||
            reason?.Any(character => char.IsControl(character) || !(char.IsLetterOrDigit(character) || character is '-' or '_')) == true)
            return BadRequest(new { error = "recommendation_feedback_invalid" });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var candidate = await db.RecommendationCandidates.AsNoTracking()
            .Join(db.RecommendationRuns.AsNoTracking(), item => item.RunId, run => run.Id, (item, run) => new { item, run })
            .SingleOrDefaultAsync(value => value.item.Id == candidateId &&
                value.item.TenantId == scope.TenantId && value.item.OwnerUserId == scope.OwnerUserId &&
                value.run.Protocol == scope.Protocol && value.run.BackendInstanceId == scope.BackendInstanceId &&
                value.run.LibraryScopeId == scope.LibraryScopeId, cancellationToken);
        if (candidate == null) return NotFound();
        var feedback = await db.RecommendationFeedback.SingleOrDefaultAsync(item =>
            item.CandidateId == candidateId && item.TenantId == scope.TenantId &&
            item.OwnerUserId == scope.OwnerUserId, cancellationToken);
        if (feedback == null)
        {
            if (request.ExpectedRevision != 0) return Conflict(new { error = "recommendation_feedback_revision_conflict" });
            feedback = new()
            {
                Id = Guid.CreateVersion7(),
                CandidateId = candidateId,
                TenantId = scope.TenantId,
                OwnerUserId = scope.OwnerUserId,
                Protocol = scope.Protocol,
                BackendInstanceId = scope.BackendInstanceId,
                LibraryScopeId = scope.LibraryScopeId,
                TrackKey = candidate.item.TrackKey,
                CreatedAt = _clock?.UtcNow ?? DateTimeOffset.UtcNow,
                Revision = 1
            };
            db.RecommendationFeedback.Add(feedback);
        }
        else
        {
            if (feedback.Revision != request.ExpectedRevision)
                return Conflict(new { error = "recommendation_feedback_revision_conflict" });
            feedback.Revision++;
        }
        feedback.Kind = kind;
        feedback.ReasonCode = reason;
        feedback.UpdatedAt = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { feedback.Kind, feedback.ReasonCode, feedback.UpdatedAt, feedback.Revision });
    }

    [HttpPost("schedules")]
    public async Task<IActionResult> CreateSchedule([FromBody] IntelligenceScheduleRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!TryScheduleRequest(request, out var template, out var overlap, out var misfire, out var scheduleError))
            return BadRequest(new { error = scheduleError });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var policy = await IntelligencePolicyService.Query(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (policy?.Enabled != true) return Conflict(new { error = "intelligence_not_ready" });
        template = template with { IntelligencePolicyId = policy.Id };
        var now = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        var schedule = new JobScheduleRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            LibraryScopeId = scope.LibraryScopeId,
            JobType = DurableScheduleEngine.RecommendationJobType,
            CronExpression = request.CronExpression.Trim(),
            TimeZoneId = request.TimeZoneId.Trim(),
            OverlapPolicy = overlap,
            MisfirePolicy = misfire,
            RetryPolicyJson = "{}",
            PayloadTemplateJson = JsonSerializer.Serialize(template),
            Enabled = request.Enabled,
            NextRunAt = request.Enabled ? DurableScheduleEngine.GetNextOccurrence(
                request.CronExpression, request.TimeZoneId, now) : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.JobSchedules.Add(schedule);
        await db.SaveChangesAsync(cancellationToken);
        return Created($"/api/admin/intelligence/schedules/{schedule.Id}", ToScheduleDto(schedule, template));
    }

    [HttpPut("schedules/{scheduleId:guid}")]
    public async Task<IActionResult> UpdateSchedule(Guid scheduleId, [FromBody] IntelligenceScheduleRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ExpectedRevision.HasValue) return BadRequest(new { error = "ExpectedRevision is required" });
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!TryScheduleRequest(request, out var template, out var overlap, out var misfire, out var scheduleError))
            return BadRequest(new { error = scheduleError });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var policy = await IntelligencePolicyService.Query(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (policy == null || request.Enabled && !policy.Enabled)
            return Conflict(new { error = "intelligence_not_ready" });
        template = template with { IntelligencePolicyId = policy.Id };
        var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == scheduleId &&
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.LibraryScopeId == scope.LibraryScopeId && item.JobType == DurableScheduleEngine.RecommendationJobType,
            cancellationToken);
        if (schedule == null) return NotFound();
        RecommendationScheduleTemplate existingTemplate;
        try { existingTemplate = ParseScheduleTemplate(schedule.PayloadTemplateJson); }
        catch (JsonException) { return NotFound(); }
        if (existingTemplate.Version != 1 || existingTemplate.IntelligencePolicyId != policy.Id)
            return NotFound();
        if (schedule.Revision != request.ExpectedRevision) return Conflict(new { error = "intelligence_schedule_revision_conflict" });
        var now = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        schedule.CronExpression = request.CronExpression.Trim();
        schedule.TimeZoneId = request.TimeZoneId.Trim();
        schedule.OverlapPolicy = overlap;
        schedule.MisfirePolicy = misfire;
        schedule.PayloadTemplateJson = JsonSerializer.Serialize(template);
        schedule.Enabled = request.Enabled;
        schedule.NextRunAt = request.Enabled ? DurableScheduleEngine.GetNextOccurrence(
            request.CronExpression, request.TimeZoneId, now) : null;
        schedule.UpdatedAt = now;
        schedule.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToScheduleDto(schedule, template));
    }

    [HttpDelete("schedules/{scheduleId:guid}")]
    public async Task<IActionResult> DeleteSchedule(Guid scheduleId,
        [FromBody] IntelligenceScheduleDeleteRequest request, CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == scheduleId &&
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.LibraryScopeId == scope.LibraryScopeId && item.JobType == DurableScheduleEngine.RecommendationJobType,
            cancellationToken);
        if (schedule == null) return NotFound();
        var policy = await IntelligencePolicyService.Query(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (policy == null) return NotFound();
        var template = TryParseScheduleTemplate(schedule.PayloadTemplateJson);
        if (template == null) return NotFound();
        if (template.Version != 1 || template.IntelligencePolicyId != policy.Id)
            return NotFound();
        if (schedule.Revision != request.ExpectedRevision)
            return Conflict(new { error = "intelligence_schedule_revision_conflict" });
        schedule.Enabled = false;
        schedule.NextRunAt = null;
        schedule.UpdatedAt = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        schedule.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
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
    private static RecommendationScheduleTemplate ParseScheduleTemplate(string json) =>
        JsonSerializer.Deserialize<RecommendationScheduleTemplate>(json) ?? throw new JsonException();
    private static RecommendationScheduleTemplate? TryParseScheduleTemplate(string json)
    {
        try { return ParseScheduleTemplate(json); }
        catch (JsonException) { return null; }
    }
    private static bool TryScheduleRequest(IntelligenceScheduleRequest request,
        out RecommendationScheduleTemplate template, out ScheduleOverlapPolicy overlap,
        out ScheduleMisfirePolicy misfire, out string? error)
    {
        template = null!;
        error = null;
        if (!Enum.TryParse(request.OverlapPolicy, true, out overlap) || !Enum.IsDefined(overlap))
        { misfire = default; error = "OverlapPolicy must be skip or queue"; return false; }
        if (!Enum.TryParse(request.MisfirePolicy, true, out misfire) || !Enum.IsDefined(misfire))
        { error = "MisfirePolicy must be skip or runOnce"; return false; }
        try { DurableScheduleEngine.Validate(request.CronExpression, request.TimeZoneId); }
        catch (ArgumentException exception) { error = exception.Message; return false; }
        if (request.Limit is < 1 or > 500) { error = "Limit must be between 1 and 500"; return false; }
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl))
        { error = "Name is required and must be at most 200 characters"; return false; }
        template = new(1, Guid.Empty, request.Limit, name);
        return true;
    }
    private static object ToScheduleDto(JobScheduleRecord schedule, RecommendationScheduleTemplate template) => new
    {
        id = schedule.Id,
        schedule.CronExpression,
        schedule.TimeZoneId,
        overlapPolicy = schedule.OverlapPolicy.ToString().ToLowerInvariant(),
        misfirePolicy = char.ToLowerInvariant(schedule.MisfirePolicy.ToString()[0]) + schedule.MisfirePolicy.ToString()[1..],
        schedule.Enabled,
        schedule.NextRunAt,
        schedule.Revision,
        name = template.GeneratedSetName,
        template.Limit,
    };
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
        listeningServices = Array.Empty<object>(),
        songDetails = new { pending = 0, resolved = 0, unresolved = 0, failed = 0 },
        candidates = Array.Empty<object>(),
        generatedSets = Array.Empty<object>(),
        schedules = Array.Empty<object>(),
        visualization = Array.Empty<object>(),
        actions = new { canRun = false, canGenerate = false }
    };
    private static object PublicScope(IntelligenceScope scope) => new { scope.Protocol, scope.BackendInstanceId, scope.LibraryScopeId };
    private static string Label(string value) => value switch
    {
        "listenbrainz" => "ListenBrainz recommendations",
        "listenbrainz-weekly-exploration" => "ListenBrainz Weekly Exploration",
        "listenbrainz-weekly-jams" => "ListenBrainz Weekly Jams",
        "listenbrainz-top-recordings" => "Your ListenBrainz top tracks",
        _ => string.Join(' ', value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => char.ToUpperInvariant(item[0]) + item[1..]))
    };
    private static string PlaybackTargetLabel(string value) => value switch
    {
        "lastfm" => "Last.fm",
        "listenbrainz" => "ListenBrainz",
        _ => Label(value)
    };
    private static string SourceDescription(string id) => id switch
    {
        "lastfm" => "Uses your connected Last.fm listening history to suggest songs.",
        "audiomuse-ai" => "Finds songs that sound alike through the optional AudioMuse-AI connection.",
        "musicbrainz-local" => "Finds related songs from this library's genres, performers, and credits. It does not send listening history to MusicBrainz.",
        "jellyfin-instant-mix" => "Your linked Jellyfin library's Instant Mix results.",
        "listenbrainz" => "Uses your connected ListenBrainz listening history to suggest songs.",
        "listenbrainz-weekly-exploration" => "Tracks from the latest Weekly Exploration playlist ListenBrainz made for you.",
        "listenbrainz-weekly-jams" => "Tracks from the latest Weekly Jams playlist ListenBrainz made for you.",
        "listenbrainz-top-recordings" => "Tracks you played most on ListenBrainz this month.",
        "local-rules" => "Uses saved listening and library activity without sending it to another service.",
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
public sealed class IntelligenceFeedbackRequest : IntelligenceScopeRequest
{
    public string Kind { get; set; } = "";
    public string? ReasonCode { get; set; }
    public long ExpectedRevision { get; set; }
}
public sealed class IntelligenceScheduleRequest : IntelligenceScopeRequest
{
    public string Name { get; set; } = "";
    public int Limit { get; set; } = 25;
    public string CronExpression { get; set; } = "0 8 * * *";
    public string TimeZoneId { get; set; } = "UTC";
    public string OverlapPolicy { get; set; } = "skip";
    public string MisfirePolicy { get; set; } = "runOnce";
    public bool Enabled { get; set; } = true;
    public long? ExpectedRevision { get; set; }
}
public sealed class IntelligenceScheduleDeleteRequest : IntelligenceScopeRequest
{
    public long ExpectedRevision { get; set; }
}
