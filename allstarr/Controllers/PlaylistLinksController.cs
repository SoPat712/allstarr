using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/playlist-links")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class PlaylistLinksController(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IPlaylistPersistenceService playlists,
    ITrackMatchPersistenceService matches,
    PlaylistOrchestrationService orchestration,
    DurableJobQueue jobs,
    EncryptedSecretStore secretStore,
    IPlatformClock clock) : ControllerBase
{
    private const string SubsonicCredentialPurpose = "playlist-backend:subsonic";
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? libraryScopeId, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var context = await CreateExecutionAsync(session, libraryScopeId, cancellationToken);
            return Ok(new { playlistLinks = (await playlists.ListLinksAsync(context, libraryScopeId, cancellationToken)).Select(ToDto) });
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePlaylistLinkRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            if (!TryEnums(request.Mode, request.MaterializationMode, out var mode, out var materialization, out var error)) return BadRequest(new { error });
            if (!ValidTargetProtocol(request.TargetProtocol)) return BadRequest(new { error = "TargetProtocol must be jellyfin or subsonic" });
            var context = await CreateExecutionAsync(session, request.LibraryScopeId, cancellationToken);
            if (!await CredentialReferenceAllowed(context, request.TargetCredentialReferenceId, cancellationToken)) return BadRequest(new { error = "TargetCredentialReferenceId is unavailable in this tenant" });
            if (!await TargetIdentityAllowed(context, request.TargetProtocol, request.TargetBackendInstanceId, cancellationToken)) return BadRequest(new { error = "The target backend identity is not linked to this user" });
            if (!await ScheduleAllowed(context, request.ScheduleId, request.LibraryScopeId, cancellationToken)) return BadRequest(new { error = "ScheduleId is unavailable in this owner and library scope" });
            var source = Required(request.SourcePlaylistId, nameof(request.SourcePlaylistId));
            var record = await playlists.CreateLinkAsync(context, new PlaylistLinkInput(
                request.ProviderAccountId, Required(request.SourceProviderId, nameof(request.SourceProviderId)).ToLowerInvariant(), source,
                Hash(source), Required(request.LibraryScopeId, nameof(request.LibraryScopeId)), request.TargetProtocol.Trim().ToLowerInvariant(),
                Required(request.TargetBackendInstanceId, nameof(request.TargetBackendInstanceId)), mode, materialization,
                "playlist-rules-v1", "playlist-policy-v1", request.ScheduleId, request.TargetPlaylistId,
                request.TargetCredentialReferenceId, request.MirrorStaleEntries, request.PreserveManualEntries,
                request.SyncName, request.SyncDescription, request.SyncArtwork), cancellationToken);
            return CreatedAtAction(nameof(List), new { libraryScopeId = record.LibraryScopeId }, ToDto(record));
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePlaylistLinkRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            if (!TryEnums(request.Mode, request.MaterializationMode, out var mode, out var materialization, out var error)) return BadRequest(new { error });
            var existing = await LoadScopedLink(session, id, cancellationToken);
            var context = await CreateExecutionAsync(session, existing.LibraryScopeId, cancellationToken);
            if (!await CredentialReferenceAllowed(context, request.TargetCredentialReferenceId, cancellationToken)) return BadRequest(new { error = "TargetCredentialReferenceId is unavailable in this tenant" });
            if (!await ScheduleAllowed(context, request.ScheduleId, existing.LibraryScopeId, cancellationToken)) return BadRequest(new { error = "ScheduleId is unavailable in this owner and library scope" });
            var updated = await playlists.UpdateLinkAsync(context, id, new PlaylistLinkUpdate(
                request.ExpectedRevision, mode, materialization, request.RuleVersion ?? existing.RuleVersion,
                request.PolicyVersion ?? existing.PolicyVersion, request.ScheduleId, request.TargetPlaylistId,
                request.MirrorStaleEntries, request.PreserveManualEntries, request.SyncName,
                request.SyncDescription, request.SyncArtwork, request.TargetCredentialReferenceId), cancellationToken);
            return Ok(ToDto(updated));
        });
    }

    [HttpPost("{id:guid}/refresh")]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var link = await LoadScopedLink(session, id, cancellationToken);
            var context = await CreateExecutionAsync(session, link.LibraryScopeId, cancellationToken);
            var refreshed = await orchestration.RefreshAsync(context, id, cancellationToken: cancellationToken);
            var preview = await playlists.ReadPreviewAsync(context, id, refreshed.SnapshotId, cancellationToken);
            return Ok(new { snapshot = new { snapshotId = refreshed.SnapshotId, snapshotVersion = refreshed.SnapshotVersion, sourceRevision = refreshed.SourceRevision }, preview = ToPreviewDto(preview) });
        });
    }

    [HttpGet("{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, [FromQuery] Guid snapshotId, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var link = await LoadScopedLink(session, id, cancellationToken);
            var context = await CreateExecutionAsync(session, link.LibraryScopeId, cancellationToken);
            return Ok(ToPreviewDto(await playlists.ReadPreviewAsync(context, id, snapshotId, cancellationToken)));
        });
    }

    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> Run(Guid id, [FromBody] RunPlaylistLinkRequest? request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var link = await LoadScopedLink(session, id, cancellationToken);
            var generation = request?.Generation ?? clock.UtcNow.UtcTicks;
            if (generation <= 0) return BadRequest(new { error = "Generation must be positive" });
            var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
                "playlist.materialize", $"manual:{id:N}:generation:{generation}",
                new PlaylistMaterializationJobPayload(id, generation, request?.SnapshotId),
                link.TenantId, link.OwnerUserId, ProviderAccountId: link.ProviderAccountId,
                LibraryScopeId: link.LibraryScopeId, Capability: "playlist",
                CorrelationId: HttpContext.TraceIdentifier), cancellationToken);
            return Accepted(new { jobId = result.JobId, created = result.Created, generation });
        });
    }

    [HttpPost("matches/{externalSnapshotId:guid}/override")]
    public async Task<IActionResult> SetOverride(Guid externalSnapshotId, [FromBody] SetMatchOverrideRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            if (!Enum.TryParse<ManualOverrideDecision>(request.Decision, true, out var decision)) return BadRequest(new { error = "Decision must be pin or reject" });
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item => item.Id == externalSnapshotId, cancellationToken) ?? throw new KeyNotFoundException("External snapshot not found.");
            EnsureSessionScope(session, snapshot.TenantId, snapshot.OwnerUserId);
            var context = await CreateExecutionAsync(session, snapshot.LibraryScopeId, cancellationToken);
            var value = await matches.SetOverrideAsync(context, new ManualOverrideInput(externalSnapshotId,
                snapshot.LibraryScopeId, decision, request.LibraryTrackId, Required(request.Reason, nameof(request.Reason))), cancellationToken);
            return Ok(value);
        });
    }

    [HttpDelete("matches/overrides/{overrideId:guid}")]
    public async Task<IActionResult> ClearOverride(Guid overrideId, [FromQuery] long? expectedRevision,
        [FromBody] ClearMatchOverrideRequest? request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var revision = expectedRevision ?? request?.ExpectedRevision;
            if (!revision.HasValue) return BadRequest(new { error = "ExpectedRevision is required" });
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var value = await db.ManualTrackOverrides.AsNoTracking().SingleOrDefaultAsync(item => item.Id == overrideId, cancellationToken) ?? throw new KeyNotFoundException("Override not found.");
            EnsureSessionScope(session, value.TenantId, value.OwnerUserId);
            var context = await CreateExecutionAsync(session, value.LibraryScopeId, cancellationToken);
            await matches.RevokeOverrideAsync(context, overrideId, revision.Value, cancellationToken);
            return NoContent();
        });
    }

    [HttpPost("{id:guid}/schedules")]
    public async Task<IActionResult> CreateSchedule(Guid id, [FromBody] ScheduleRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var link = await LoadScopedLink(session, id, cancellationToken);
            if (!TryScheduleEnums(request, out var overlap, out var misfire, out var error)) return BadRequest(new { error });
            DurableScheduleEngine.Validate(request.CronExpression, request.TimeZoneId);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var now = clock.UtcNow;
            var schedule = new JobScheduleRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = link.TenantId,
                OwnerUserId = link.OwnerUserId,
                LibraryScopeId = link.LibraryScopeId,
                JobType = DurableScheduleEngine.PlaylistSyncJobType,
                CronExpression = request.CronExpression.Trim(),
                TimeZoneId = request.TimeZoneId.Trim(),
                OverlapPolicy = overlap,
                MisfirePolicy = misfire,
                RetryPolicyJson = "{}",
                PayloadTemplateJson = "{}",
                Enabled = request.Enabled,
                NextRunAt = request.Enabled ? DurableScheduleEngine.GetNextOccurrence(request.CronExpression, request.TimeZoneId, now) : null,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.JobSchedules.Add(schedule);
            var tracked = await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id && item.TenantId == link.TenantId, cancellationToken);
            if (tracked.ScheduleId.HasValue) return Conflict(new { error = "The playlist link already has a schedule" });
            tracked.ScheduleId = schedule.Id; tracked.UpdatedAt = now; tracked.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            return Created($"/api/admin/playlist-links/schedules/{schedule.Id}", ToScheduleDto(schedule));
        });
    }

    [HttpPut("schedules/{scheduleId:guid}")]
    public async Task<IActionResult> UpdateSchedule(Guid scheduleId, [FromBody] ScheduleRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            if (!request.ExpectedRevision.HasValue) return BadRequest(new { error = "ExpectedRevision is required" });
            if (!TryScheduleEnums(request, out var overlap, out var misfire, out var error)) return BadRequest(new { error });
            DurableScheduleEngine.Validate(request.CronExpression, request.TimeZoneId);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == scheduleId, cancellationToken) ?? throw new KeyNotFoundException("Schedule not found.");
            EnsureSessionScope(session, schedule.TenantId, schedule.OwnerUserId);
            if (schedule.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The schedule changed before this update.");
            schedule.CronExpression = request.CronExpression.Trim(); schedule.TimeZoneId = request.TimeZoneId.Trim();
            schedule.OverlapPolicy = overlap; schedule.MisfirePolicy = misfire; schedule.Enabled = request.Enabled;
            schedule.NextRunAt = request.Enabled ? DurableScheduleEngine.GetNextOccurrence(request.CronExpression, request.TimeZoneId, clock.UtcNow) : null;
            schedule.UpdatedAt = clock.UtcNow; schedule.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(ToScheduleDto(schedule));
        });
    }

    [HttpPost("backend-credentials")]
    public async Task<IActionResult> CreateBackendCredential([FromBody] BackendCredentialRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            if (!ValidCredentialRequest(request, out var error)) return BadRequest(new { error });
            var info = await StoreCredential(session, request, null, cancellationToken);
            return Created($"/api/admin/playlist-links/backend-credentials/{info.Id}", ToCredentialDto(info));
        });
    }

    [HttpPut("backend-credentials/{referenceId:guid}")]
    public async Task<IActionResult> RotateBackendCredential(Guid referenceId, [FromBody] BackendCredentialRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            if (!ValidCredentialRequest(request, out var error)) return BadRequest(new { error });
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.SecretReferences.AsNoTracking().SingleOrDefaultAsync(item => item.Id == referenceId, cancellationToken)
                ?? throw new KeyNotFoundException("Credential reference not found.");
            if (existing.TenantId != session.TenantId || existing.Purpose != SubsonicCredentialPurpose)
                throw new UnauthorizedAccessException();
            return Ok(ToCredentialDto(await StoreCredential(session, request, referenceId, cancellationToken)));
        });
    }

    private async Task<IActionResult> Execute(Func<AdminAuthSession, Task<IActionResult>> action)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession session)
            return Unauthorized(new { error = "Authentication required" });
        if (!session.TenantId.HasValue || !session.AllstarrUserId.HasValue)
            return StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" });
        try { return await action(session); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "The resource is outside the authenticated scope" }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The resource changed before this update" }); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private async Task<ProtocolExecutionContext> CreateExecutionAsync(AdminAuthSession session, string? libraryScopeId, CancellationToken cancellationToken)
    {
        var tenantId = session.TenantId!.Value; var userId = session.AllstarrUserId!.Value;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var backendType = session.BackendType.Trim().ToLowerInvariant();
        var identity = await db.BackendIdentities.AsNoTracking().Where(item => item.TenantId == tenantId && item.UserId == userId && item.BackendType == backendType && item.PrincipalId == session.UserId)
            .OrderByDescending(item => item.LastSeenAt).FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The linked backend identity is unavailable.");
        var protocol = backendType == "jellyfin" ? ProtocolKind.Jellyfin : backendType is "subsonic" or "navidrome" or "opensubsonic" ? ProtocolKind.Subsonic : throw new UnauthorizedAccessException("Unsupported backend identity.");
        var principal = new AllstarrPrincipal(tenantId, userId, protocol.ToString().ToLowerInvariant(), identity.BackendInstanceId,
            identity.PrincipalId, session.UserName, session.IsAdministrator);
        return new ProtocolExecutionContext(protocol, identity.BackendInstanceId, identity.PrincipalId, principal,
            HttpContext.TraceIdentifier.Length <= 100 ? HttpContext.TraceIdentifier : HttpContext.TraceIdentifier[..100],
            clock.UtcNow.AddMinutes(5), cancellationToken,
            libraryScopeId: string.IsNullOrWhiteSpace(libraryScopeId) ? null : libraryScopeId.Trim());
    }

    private async Task<PlaylistLinkRecord> LoadScopedLink(AdminAuthSession session, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken) ?? throw new KeyNotFoundException();
        EnsureSessionScope(session, link.TenantId, link.OwnerUserId); return link;
    }

    private static void EnsureSessionScope(AdminAuthSession session, Guid tenantId, Guid ownerUserId)
    { if (session.TenantId != tenantId || (!session.IsAdministrator && session.AllstarrUserId != ownerUserId)) throw new UnauthorizedAccessException(); }

    private async Task<bool> CredentialReferenceAllowed(ProtocolExecutionContext context, Guid? id, CancellationToken cancellationToken)
    {
        if (!id.HasValue) return true;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SecretReferences.AsNoTracking().AnyAsync(item => item.Id == id && item.RevokedAt == null &&
            item.TenantId == context.Actor!.TenantId && item.Purpose == SubsonicCredentialPurpose, cancellationToken);
    }

    private async Task<SecretReferenceInfo> StoreCredential(AdminAuthSession session, BackendCredentialRequest request,
        Guid? existingReferenceId, CancellationToken cancellationToken)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { username = request.Username.Trim(), password = request.Password });
        try { return await secretStore.StoreAsync(session.TenantId, SubsonicCredentialPurpose, bytes, existingReferenceId, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool ValidCredentialRequest(BackendCredentialRequest request, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(request.TargetProtocol) || !request.TargetProtocol.Trim().Equals("subsonic", StringComparison.OrdinalIgnoreCase)) { error = "TargetProtocol must be subsonic"; return false; }
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 300) { error = "Username is required and must be at most 300 characters"; return false; }
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 2000) { error = "Password is required and must be at most 2000 characters"; return false; }
        return true;
    }

    private async Task<bool> TargetIdentityAllowed(ProtocolExecutionContext context, string targetProtocol, string backendInstanceId, CancellationToken cancellationToken)
    {
        var actor = context.RequireActor();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.BackendIdentities.AsNoTracking().AnyAsync(item => item.TenantId == actor.TenantId &&
            item.UserId == actor.EffectiveUserId && item.BackendType == targetProtocol.Trim().ToLowerInvariant() &&
            item.BackendInstanceId == backendInstanceId.Trim(), cancellationToken);
    }

    private async Task<bool> ScheduleAllowed(ProtocolExecutionContext context, Guid? id, string libraryScopeId, CancellationToken cancellationToken)
    {
        if (!id.HasValue) return true;
        var actor = context.RequireActor();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.JobSchedules.AsNoTracking().AnyAsync(item => item.Id == id && item.TenantId == actor.TenantId &&
            item.OwnerUserId == actor.EffectiveUserId && item.LibraryScopeId == libraryScopeId, cancellationToken);
    }

    private static bool TryEnums(string modeValue, string materializationValue, out PlaylistLinkMode mode, out PlaylistMaterializationMode materialization, out string? error)
    { error = null; if (!Enum.TryParse(modeValue, true, out mode) || !Enum.IsDefined(mode)) { materialization = default; error = "Mode must be virtual, materialized, or hybrid"; return false; } if (!Enum.TryParse(materializationValue, true, out materialization) || !Enum.IsDefined(materialization)) { error = "MaterializationMode must be reconcile or recreate"; return false; } return true; }
    private static bool TryScheduleEnums(ScheduleRequest request, out ScheduleOverlapPolicy overlap, out ScheduleMisfirePolicy misfire, out string? error)
    { error = null; if (!Enum.TryParse(request.OverlapPolicy, true, out overlap) || !Enum.IsDefined(overlap)) { misfire = default; error = "OverlapPolicy must be skip or queue"; return false; } if (!Enum.TryParse(request.MisfirePolicy, true, out misfire) || !Enum.IsDefined(misfire)) { error = "MisfirePolicy must be skip or runOnce"; return false; } return true; }
    private static bool ValidTargetProtocol(string value) => value?.Trim().ToLowerInvariant() is "jellyfin" or "subsonic";
    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException($"{name} is required");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static object ToDto(PlaylistLinkRecord value) => new { id = value.Id, providerAccountId = value.ProviderAccountId, sourceProviderId = value.SourceProviderId, sourcePlaylistId = value.SourcePlaylistId, libraryScopeId = value.LibraryScopeId, targetProtocol = value.TargetProtocol, targetBackendInstanceId = value.TargetBackendInstanceId, mode = value.Mode.ToString().ToLowerInvariant(), materializationMode = value.MaterializationMode.ToString().ToLowerInvariant(), scheduleId = value.ScheduleId, targetPlaylistId = value.TargetPlaylistId, targetCredentialReferenceId = value.TargetCredentialReferenceId, mirrorStaleEntries = value.MirrorStaleEntries, preserveManualEntries = value.PreserveManualEntries, syncName = value.SyncName, syncDescription = value.SyncDescription, syncArtwork = value.SyncArtwork, ruleVersion = value.RuleVersion, policyVersion = value.PolicyVersion, revision = value.Revision, virtualPlaylistId = PlaylistVirtualizationService.CreateProtocolId(value.Id) };
    private static object ToScheduleDto(JobScheduleRecord value) => new { id = value.Id, cronExpression = value.CronExpression, timeZoneId = value.TimeZoneId, overlapPolicy = value.OverlapPolicy.ToString().ToLowerInvariant(), misfirePolicy = LowerCamel(value.MisfirePolicy.ToString()), enabled = value.Enabled, nextRunAt = value.NextRunAt, revision = value.Revision };
    private static object ToPreviewDto(PlaylistPreview value) => new { linkId = value.LinkId, snapshotId = value.SnapshotId, name = value.Name, description = value.Description, artworkReferenceKey = value.ArtworkReferenceKey, entries = value.Entries.Select(item => new { position = item.Position, externalSnapshotId = item.ExternalSnapshotId, state = item.State.ToString().ToLowerInvariant(), libraryTrackId = item.LibraryTrackId, @override = item.Override?.ToString().ToLowerInvariant() }) };
    private static object ToCredentialDto(SecretReferenceInfo value) => new { referenceId = value.Id, targetProtocol = "subsonic", purpose = value.Purpose, activeVersion = value.ActiveVersion, updatedAt = value.UpdatedAt };
    private static string LowerCamel(string value) => char.ToLowerInvariant(value[0]) + value[1..];
}

public sealed record CreatePlaylistLinkRequest(Guid ProviderAccountId, string SourceProviderId, string SourcePlaylistId,
    string LibraryScopeId, string TargetProtocol, string TargetBackendInstanceId, string Mode, string MaterializationMode,
    Guid? ScheduleId = null, string? TargetPlaylistId = null, Guid? TargetCredentialReferenceId = null,
    bool MirrorStaleEntries = false, bool PreserveManualEntries = true, bool SyncName = true,
    bool SyncDescription = true, bool SyncArtwork = true);
public sealed record UpdatePlaylistLinkRequest(long ExpectedRevision, string Mode, string MaterializationMode,
    Guid? ScheduleId, string? TargetPlaylistId, Guid? TargetCredentialReferenceId, bool MirrorStaleEntries,
    bool PreserveManualEntries, bool SyncName, bool SyncDescription, bool SyncArtwork,
    string? RuleVersion = null, string? PolicyVersion = null);
public sealed record RunPlaylistLinkRequest(long? Generation = null, Guid? SnapshotId = null);
public sealed record SetMatchOverrideRequest(string Decision, Guid? LibraryTrackId, string Reason);
public sealed record ClearMatchOverrideRequest(long ExpectedRevision);
public sealed record ScheduleRequest(string CronExpression, string TimeZoneId, string OverlapPolicy,
    string MisfirePolicy, bool Enabled = true, long? ExpectedRevision = null);
public sealed class BackendCredentialRequest
{
    public string TargetProtocol { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
