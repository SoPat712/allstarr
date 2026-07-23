using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
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
    IProviderRegistry providerRegistry,
    IProviderRouter providerRouter,
    IBackendPlaylistTargetResolver targetResolver,
    IPlatformClock clock,
    ProviderPolicyOptions providerPolicy) : ControllerBase
{
    private const string SubsonicCredentialPurpose = "playlist-backend:subsonic";

    [HttpGet("/api/admin/playlist-sources")]
    public async Task<IActionResult> ListPlaylistSources(CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var supportedProviders = providerRegistry
                .FindByCapability(ProviderCapabilityKind.Playlist, includeNonOperational: false)
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var accounts = await db.ProviderAccounts.AsNoTracking()
                .Where(item => item.Enabled &&
                               (item.TenantId == null || item.TenantId == session.TenantId) &&
                               (item.OwnerUserId == null || item.OwnerUserId == session.AllstarrUserId))
                .OrderBy(item => item.ProviderId)
                .ThenBy(item => item.DisplayName)
                .ToListAsync(cancellationToken);
            var ownerIds = accounts.Where(item => item.OwnerUserId.HasValue).Select(item => item.OwnerUserId!.Value).Distinct().ToArray();
            var ownerNames = await db.Users.AsNoTracking()
                .Where(item => ownerIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
            var capableAccounts = accounts.Where(item =>
            {
                if (!supportedProviders.TryGetValue(item.ProviderId, out var provider)) return false;
                var capability = provider.Capabilities.Single(value => value.Capability == ProviderCapabilityKind.Playlist);
                return capability.AllowedAccountScopes.Contains(item.Scope);
            }).ToArray();
            var availableAccounts = capableAccounts
                .Where(item => item.Scope != ProviderAccountScope.Global ||
                               providerPolicy.AllowGlobalPersonalAccounts ||
                               session.IsAdministrator)
                .ToArray();
            var blockedAccounts = capableAccounts.Except(availableAccounts).ToArray();
            return Ok(new
            {
                accounts = availableAccounts.Select(item => ToPlaylistSourceAccountDto(
                    item,
                    true,
                    null,
                    item.OwnerUserId.HasValue ? ownerNames.GetValueOrDefault(item.OwnerUserId.Value) : null,
                    item.Scope == ProviderAccountScope.Global &&
                    session.IsAdministrator &&
                    !providerPolicy.AllowGlobalPersonalAccounts)),
                blockedAccounts = blockedAccounts.Select(item => ToPlaylistSourceAccountDto(
                    item,
                    false,
                    "shared-playlist-credentials-disabled",
                    item.OwnerUserId.HasValue ? ownerNames.GetValueOrDefault(item.OwnerUserId.Value) : null)),
                providers = supportedProviders.Values.Select(provider =>
                {
                    var capability = provider.Capabilities.Single(value => value.Capability == ProviderCapabilityKind.Playlist);
                    return new
                    {
                        id = provider.Id,
                        displayName = provider.DisplayName,
                        origin = provider.Origin.ToString().ToLowerInvariant(),
                        accountRequirement = capability.AccountRequirement.ToString().ToLowerInvariant()
                    };
                }),
                policy = new
                {
                    allowSharedPlaylistCredentials = providerPolicy.AllowGlobalPersonalAccounts,
                    administratorCanUseSharedPlaylistCredentials = session.IsAdministrator
                }
            });
        });
    }

    [HttpGet("/api/admin/playlist-sources/{accountId:guid}/playlists")]
    public async Task<IActionResult> BrowseSourcePlaylists(
        Guid accountId,
        [FromQuery] string? query,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 30,
        CancellationToken cancellationToken = default)
    {
        return await Execute(async session =>
        {
            limit = Math.Clamp(limit, 1, 100);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == accountId && item.Enabled &&
                        (item.TenantId == null || item.TenantId == session.TenantId) &&
                        (item.OwnerUserId == null || item.OwnerUserId == session.AllstarrUserId),
                cancellationToken) ?? throw new KeyNotFoundException();
            var execution = await CreateExecutionAsync(session, account.LibraryScopeId, cancellationToken);
            var actor = execution.RequireActor();
            var providerId = account.ProviderId.Trim().ToLowerInvariant();
            var policy = new ProviderExecutionPolicy(
                new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, allowTranscode: false),
                ProviderExplicitContentPolicy.Allow,
                allowFallback: false,
                allowSharedAccount: true,
                allowManagedDownloads: false,
                allowedProviderIds: [providerId]);
            var library = string.IsNullOrWhiteSpace(account.LibraryScopeId)
                ? null
                : new ProviderLibraryContext(actor.TenantId, account.LibraryScopeId);
            var plan = await providerRouter.PlanAsync<IProviderPlaylistCapability>(new ProviderRouteRequest(
                ProviderCapabilityKind.Playlist,
                actor,
                policy,
                "playlist-source-discovery",
                HttpContext.TraceIdentifier,
                clock.UtcNow.AddMinutes(2),
                [providerId],
                [new ProviderRouteProviderState(providerId, requestedAccountId: account.Id, expectedAccountRevision: account.Revision)],
                library: library,
                cancellationToken: cancellationToken));
            var candidate = plan.Candidates.FirstOrDefault();
            if (candidate == null)
                return Conflict(new { error = "The selected account cannot currently browse playlists", reasonCode = plan.Decision.Candidates.FirstOrDefault()?.ReasonCode });

            var pageRequest = new ProviderPageRequest(limit, cursor);
            var outcome = string.IsNullOrWhiteSpace(query)
                ? await candidate.Implementation.GetUserPlaylistsAsync(candidate.Context, new ProviderUserPlaylistsRequest(pageRequest))
                : await candidate.Implementation.SearchPlaylistsAsync(candidate.Context, new ProviderPlaylistSearchRequest(query.Trim(), pageRequest));
            if (!outcome.IsSuccess)
            {
                var failure = outcome.Error!;
                var status = failure.Kind switch
                {
                    ProviderErrorKind.AccountNeedsConfiguration => StatusCodes.Status409Conflict,
                    ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
                    ProviderErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                    ProviderErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
                    ProviderErrorKind.NotFound => StatusCodes.Status404NotFound,
                    _ => StatusCodes.Status502BadGateway
                };
                var retryAfterSeconds = failure.RetryAfter is { } retryAfter
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : (int?)null;
                if (status == StatusCodes.Status429TooManyRequests && retryAfterSeconds.HasValue)
                {
                    Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                return StatusCode(status, new
                {
                    error = failure.SafeMessage,
                    reasonCode = failure.Code,
                    providerId,
                    accountId = account.Id,
                    retryAfterSeconds
                });
            }
            var page = outcome.RequireValue();
            return Ok(new
            {
                providerId,
                accountId = account.Id,
                items = page.Items.Select(item => ToPlaylistSummaryDto(item, account.Id)),
                nextCursor = page.NextCursor,
                isPartial = page.IsPartial,
                snapshotVersion = page.SnapshotVersion
            });
        });
    }

    [HttpGet("/api/admin/playlist-sources/{accountId:guid}/playlists/{playlistId}/artwork")]
    public async Task<IActionResult> SourcePlaylistArtwork(
        Guid accountId,
        string playlistId,
        [FromQuery] string? revision,
        CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == accountId && item.Enabled &&
                        (item.TenantId == null || item.TenantId == session.TenantId) &&
                        (item.OwnerUserId == null || item.OwnerUserId == session.AllstarrUserId),
                cancellationToken) ?? throw new KeyNotFoundException();
            var candidate = await PlanPlaylistSourceAsync(session, account, "playlist-artwork", cancellationToken);
            if (candidate == null)
                return Conflict(new { error = "The selected account cannot currently load playlist artwork" });
            var providerId = account.ProviderId.Trim().ToLowerInvariant();
            var reference = new ProviderArtworkReference(
                new ProviderExternalResourceId(providerId, ProviderResourceKind.Playlist, Required(playlistId, nameof(playlistId))),
                revision: revision);
            var outcome = await candidate.Implementation.ResolveArtworkAsync(
                candidate.Context,
                new ProviderPlaylistArtworkRequest(reference, maximumBytes: 4 * 1024 * 1024));
            if (!outcome.IsSuccess)
                return NotFound(new { error = "Playlist artwork is unavailable", reasonCode = outcome.Error?.Code });
            var artwork = outcome.RequireValue();
            Response.Headers.CacheControl = "private, max-age=300";
            return File(artwork.Bytes, artwork.ContentType);
        });
    }

    [HttpGet("/api/admin/media-targets")]
    public async Task<IActionResult> ListMediaTargets(CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var identities = await db.BackendIdentities.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId && item.UserId == session.AllstarrUserId)
                .OrderByDescending(item => item.LastSeenAt)
                .ToListAsync(cancellationToken);
            var subsonicCredentialReferenceId = await db.SecretReferences.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId && item.Purpose == SubsonicCredentialPurpose && item.RevokedAt == null)
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item => (Guid?)item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            return Ok(new
            {
                targets = identities.Select(item => new
                {
                    id = item.Id,
                    protocol = NormalizeTargetProtocol(item.BackendType),
                    backendInstanceId = item.BackendInstanceId,
                    displayName = item.DisplayName ?? session.UserName,
                    principalId = item.PrincipalId,
                    credentialReferenceId = NormalizeTargetProtocol(item.BackendType) == "subsonic"
                        ? subsonicCredentialReferenceId
                        : null,
                    lastSeenAt = item.LastSeenAt
                })
            });
        });
    }

    [HttpGet("/api/admin/media-targets/{identityId:guid}/playlists")]
    public async Task<IActionResult> BrowseTargetPlaylists(
        Guid identityId,
        [FromQuery] string? query,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 30,
        CancellationToken cancellationToken = default)
    {
        return await Execute(async session =>
        {
            limit = Math.Clamp(limit, 1, 100);
            var offset = DecodeOffsetCursor(cursor);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var identity = await db.BackendIdentities.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == identityId && item.TenantId == session.TenantId && item.UserId == session.AllstarrUserId,
                cancellationToken) ?? throw new KeyNotFoundException();
            var protocol = NormalizeTargetProtocol(identity.BackendType);
            string? credentialReference = null;
            if (protocol == "subsonic")
            {
                credentialReference = await db.SecretReferences.AsNoTracking()
                    .Where(item => item.TenantId == session.TenantId && item.Purpose == SubsonicCredentialPurpose && item.RevokedAt == null)
                    .OrderByDescending(item => item.UpdatedAt)
                    .Select(item => item.Id.ToString())
                    .FirstOrDefaultAsync(cancellationToken);
                if (credentialReference == null)
                    return Conflict(new { error = "Configure this Subsonic target under Sources before selecting a playlist", reasonCode = "target-credentials-required" });
            }
            var context = new BackendPlaylistTargetContext(
                identity.BackendInstanceId,
                identity.PrincipalId,
                credentialReference,
                identity.TenantId);
            var result = await targetResolver.Resolve(protocol).ListPageAsync(context, query, offset, limit + 1, cancellationToken);
            if (!result.IsSuccess)
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "The media server could not return playlists", reasonCode = result.ErrorCode });
            var values = result.Value!;
            var page = values.Take(limit).ToArray();
            return Ok(new
            {
                targetId = identity.Id,
                protocol,
                items = page.Select(item => new
                {
                    id = item.BackendPlaylistId,
                    name = item.Name,
                    description = item.Description,
                    trackCount = item.TrackCount,
                    artworkReference = item.ArtworkReference,
                    artworkUrl = string.IsNullOrWhiteSpace(item.ArtworkReference)
                        ? null
                        : $"/api/admin/media-targets/{identity.Id}/playlists/{Uri.EscapeDataString(item.BackendPlaylistId)}/artwork?artworkReference={Uri.EscapeDataString(item.ArtworkReference)}",
                    writable = item.Writable
                }),
                nextCursor = values.Count > limit ? EncodeOffsetCursor(offset + limit) : null
            });
        });
    }

    [HttpGet("/api/admin/media-targets/{identityId:guid}/playlists/{playlistId}/artwork")]
    public async Task<IActionResult> TargetPlaylistArtwork(
        Guid identityId,
        string playlistId,
        [FromQuery] string? artworkReference,
        CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var identity = await db.BackendIdentities.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == identityId && item.TenantId == session.TenantId && item.UserId == session.AllstarrUserId,
                cancellationToken) ?? throw new KeyNotFoundException();
            var protocol = NormalizeTargetProtocol(identity.BackendType);
            string? credentialReference = null;
            if (protocol == "subsonic")
            {
                credentialReference = await db.SecretReferences.AsNoTracking()
                    .Where(item => item.TenantId == session.TenantId && item.Purpose == SubsonicCredentialPurpose && item.RevokedAt == null)
                    .OrderByDescending(item => item.UpdatedAt)
                    .Select(item => item.Id.ToString())
                    .FirstOrDefaultAsync(cancellationToken);
                if (credentialReference == null) return NotFound();
            }
            var context = new BackendPlaylistTargetContext(identity.BackendInstanceId, identity.PrincipalId, credentialReference, identity.TenantId);
            var result = await targetResolver.Resolve(protocol).ReadArtworkAsync(
                context,
                Required(playlistId, nameof(playlistId)),
                artworkReference,
                cancellationToken);
            if (!result.IsSuccess || result.Value == null) return NotFound();
            Response.Headers.CacheControl = "private, max-age=300";
            return File(result.Value.Bytes, result.Value.ContentType);
        });
    }
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? libraryScopeId, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var context = await CreateExecutionAsync(session, libraryScopeId, cancellationToken);
            var links = await playlists.ListLinksAsync(context, libraryScopeId, cancellationToken);
            var linkIds = links.Select(item => item.Id).ToArray();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var snapshots = linkIds.Length == 0 ? [] : await db.PlaylistSourceSnapshots.AsNoTracking()
                .Where(item => linkIds.Contains(item.PlaylistLinkId))
                .GroupBy(item => item.PlaylistLinkId)
                .Select(group => group.OrderByDescending(item => item.SnapshotVersion)
                    .ThenByDescending(item => item.RetrievedAt).First())
                .ToListAsync(cancellationToken);
            var runs = linkIds.Length == 0 ? [] : await db.PlaylistSyncRuns.AsNoTracking()
                .Where(item => linkIds.Contains(item.PlaylistLinkId))
                .GroupBy(item => item.PlaylistLinkId)
                .Select(group => group.OrderByDescending(item => item.Generation)
                    .ThenByDescending(item => item.StartedAt).First())
                .ToListAsync(cancellationToken);
            var snapshotsByLink = snapshots.ToDictionary(item => item.PlaylistLinkId);
            var runsByLink = runs.ToDictionary(item => item.PlaylistLinkId);
            var snapshotIds = snapshots.Select(item => item.Id).ToArray();
            var sourceEntries = snapshotIds.Length == 0 ? [] : await db.PlaylistSourceEntries.AsNoTracking()
                .Where(item => snapshotIds.Contains(item.PlaylistSourceSnapshotId))
                .ToListAsync(cancellationToken);
            var externalSnapshotIds = sourceEntries.Select(item => item.ExternalMetadataSnapshotId).Distinct().ToArray();
            var matchRows = externalSnapshotIds.Length == 0 ? [] : await db.TrackMatches.AsNoTracking()
                .Where(item => externalSnapshotIds.Contains(item.ExternalSnapshotId))
                .ToListAsync(cancellationToken);
            var latestMatches = matchRows
                .GroupBy(item => item.ExternalSnapshotId)
                .ToDictionary(group => group.Key, group => group
                    .OrderByDescending(item => item.DecisionVersion)
                    .ThenByDescending(item => item.DecidedAt)
                    .First());
            var runIds = runs.Select(item => item.Id).ToArray();
            var runEntries = runIds.Length == 0 ? [] : await db.PlaylistSyncEntryResults.AsNoTracking()
                .Where(item => runIds.Contains(item.PlaylistSyncRunId))
                .ToListAsync(cancellationToken);
            var sourceEntriesBySnapshot = sourceEntries
                .GroupBy(item => item.PlaylistSourceSnapshotId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var runEntriesByRun = runEntries
                .GroupBy(item => item.PlaylistSyncRunId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var metricsByLink = links.ToDictionary(link => link.Id, link => BuildMetrics(
                snapshotsByLink.GetValueOrDefault(link.Id),
                runsByLink.GetValueOrDefault(link.Id),
                sourceEntriesBySnapshot,
                latestMatches,
                runEntriesByRun));
            return Ok(new
            {
                playlistLinks = links.Select(link => ToListDto(link,
                snapshotsByLink.GetValueOrDefault(link.Id), runsByLink.GetValueOrDefault(link.Id),
                metricsByLink[link.Id]))
            });
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

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeletePlaylistLinkRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var existing = await LoadScopedLink(session, id, cancellationToken);
            var context = await CreateExecutionAsync(session, existing.LibraryScopeId, cancellationToken);
            await playlists.DeleteLinkAsync(context, id, request.ExpectedRevision, cancellationToken);
            return NoContent();
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

    [HttpPatch("{id:guid}/state")]
    public async Task<IActionResult> SetState(Guid id, [FromBody] SetPlaylistLinkStateRequest request, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var existing = await LoadScopedLink(session, id, cancellationToken);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var tracked = await db.PlaylistLinks.SingleAsync(item => item.Id == id && item.TenantId == existing.TenantId, cancellationToken);
            if (tracked.Revision != request.ExpectedRevision)
                throw new DbUpdateConcurrencyException("The playlist changed before its state could be updated.");
            tracked.Enabled = request.Enabled;
            tracked.UpdatedAt = clock.UtcNow;
            tracked.Revision++;
            if (tracked.ScheduleId is { } scheduleId)
            {
                var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == scheduleId && item.TenantId == tracked.TenantId, cancellationToken);
                if (schedule != null)
                {
                    schedule.Enabled = request.Enabled;
                    schedule.NextRunAt = request.Enabled
                        ? DurableScheduleEngine.GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, clock.UtcNow)
                        : null;
                    schedule.UpdatedAt = clock.UtcNow;
                    schedule.Revision++;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            return Ok(ToDto(tracked));
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
            if (!link.Enabled) return Conflict(new { error = "The playlist is paused. Resume it before running." });
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

    private async Task<ProviderRouteCandidate<IProviderPlaylistCapability>?> PlanPlaylistSourceAsync(
        AdminAuthSession session,
        ProviderAccountRecord account,
        string operationId,
        CancellationToken cancellationToken)
    {
        var execution = await CreateExecutionAsync(session, account.LibraryScopeId, cancellationToken);
        var actor = execution.RequireActor();
        var providerId = account.ProviderId.Trim().ToLowerInvariant();
        var policy = new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, allowTranscode: false),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: false,
            allowSharedAccount: true,
            allowManagedDownloads: false,
            allowedProviderIds: [providerId]);
        var library = string.IsNullOrWhiteSpace(account.LibraryScopeId)
            ? null
            : new ProviderLibraryContext(actor.TenantId, account.LibraryScopeId);
        var plan = await providerRouter.PlanAsync<IProviderPlaylistCapability>(new ProviderRouteRequest(
            ProviderCapabilityKind.Playlist,
            actor,
            policy,
            operationId,
            HttpContext.TraceIdentifier,
            clock.UtcNow.AddMinutes(2),
            [providerId],
            [new ProviderRouteProviderState(providerId, requestedAccountId: account.Id, expectedAccountRevision: account.Revision)],
            library: library,
            cancellationToken: cancellationToken));
        return plan.Candidates.FirstOrDefault();
    }

    private static object ToPlaylistSourceAccountDto(
        ProviderAccountRecord account,
        bool available,
        string? reasonCode,
        string? ownerDisplayName,
        bool administratorAccess = false) => new
        {
            id = account.Id,
            providerId = account.ProviderId,
            displayName = account.DisplayName is "Legacy .env import" or "Legacy .env import (current user)"
                ? account.ProviderId.ToLowerInvariant() switch
                {
                    "spotify" => "Spotify",
                    "apple-musickit" => "Apple Music",
                    "deezer" => "Deezer",
                    "qobuz" => "Qobuz",
                    _ => account.ProviderId
                }
                : account.DisplayName,
            ownerDisplayName,
            libraryScopeId = account.LibraryScopeId,
            scope = account.Scope.ToString().ToLowerInvariant(),
            accessLabel = account.Scope switch
            {
                ProviderAccountScope.User => "Personal account",
                ProviderAccountScope.Library => "Library-shared account",
                ProviderAccountScope.Global when administratorAccess => "Administrator account",
                _ => "Deployment-shared account"
            },
            revision = account.Revision,
            capability = "playlist",
            enabled = account.Enabled,
            available,
            reasonCode
        };

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
    private static string NormalizeTargetProtocol(string value) => value.Trim().ToLowerInvariant() switch
    {
        "jellyfin" => "jellyfin",
        "subsonic" or "navidrome" or "opensubsonic" => "subsonic",
        _ => throw new UnauthorizedAccessException("Unsupported media target protocol")
    };
    private static object ToPlaylistSummaryDto(ProviderPlaylistSummary value, Guid accountId) => new
    {
        id = value.Id.Value,
        providerId = value.Id.ProviderId,
        catalog = value.Id.Catalog,
        name = value.Name,
        description = value.Description,
        owner = value.Owner.DisplayName ?? value.Owner.ProviderUserId,
        trackCount = value.TrackCount,
        sourceRevision = value.SourceRevision,
        sourceETag = value.SourceETag,
        artworkUrl = value.Artwork?.PublicUri?.ToString() ??
                     (value.Artwork?.ResourceId == null
                         ? null
                         : $"/api/admin/playlist-sources/{accountId}/playlists/{Uri.EscapeDataString(value.Id.Value)}/artwork?revision={Uri.EscapeDataString(value.Artwork.Revision ?? value.SourceRevision)}"),
        artworkReference = value.Artwork?.ResourceId == null ? null : new
        {
            providerId = value.Artwork.ResourceId.ProviderId,
            id = value.Artwork.ResourceId.Value,
            revision = value.Artwork.Revision
        }
    };
    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException($"{name} is required");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static int DecodeOffsetCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(text, out var offset) && offset is >= 0 and <= 1_000_000
                ? offset
                : throw new ArgumentException("The playlist cursor is invalid.");
        }
        catch (FormatException)
        {
            throw new ArgumentException("The playlist cursor is invalid.");
        }
    }
    private static string EncodeOffsetCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    private static object ToDto(PlaylistLinkRecord value) => new { id = value.Id, enabled = value.Enabled, providerAccountId = value.ProviderAccountId, sourceProviderId = value.SourceProviderId, sourcePlaylistId = value.SourcePlaylistId, libraryScopeId = value.LibraryScopeId, targetProtocol = value.TargetProtocol, targetBackendInstanceId = value.TargetBackendInstanceId, mode = value.Mode.ToString().ToLowerInvariant(), materializationMode = value.MaterializationMode.ToString().ToLowerInvariant(), scheduleId = value.ScheduleId, targetPlaylistId = value.TargetPlaylistId, targetCredentialReferenceId = value.TargetCredentialReferenceId, mirrorStaleEntries = value.MirrorStaleEntries, preserveManualEntries = value.PreserveManualEntries, syncName = value.SyncName, syncDescription = value.SyncDescription, syncArtwork = value.SyncArtwork, ruleVersion = value.RuleVersion, policyVersion = value.PolicyVersion, revision = value.Revision, virtualPlaylistId = PlaylistVirtualizationService.CreateProtocolId(value.Id) };
    private static PlaylistListMetrics BuildMetrics(
        PlaylistSourceSnapshotRecord? snapshot,
        PlaylistSyncRunRecord? run,
        IReadOnlyDictionary<Guid, PlaylistSourceEntryRecord[]> entriesBySnapshot,
        IReadOnlyDictionary<Guid, TrackMatchRecord> latestMatches,
        IReadOnlyDictionary<Guid, PlaylistSyncEntryResultRecord[]> entriesByRun)
    {
        var entries = snapshot == null
            ? []
            : entriesBySnapshot.GetValueOrDefault(snapshot.Id) ?? [];
        var decisions = entries
            .Select(item => latestMatches.GetValueOrDefault(item.ExternalMetadataSnapshotId))
            .ToArray();
        var runEntries = run == null
            ? []
            : entriesByRun.GetValueOrDefault(run.Id) ?? [];
        var matched = decisions.Count(item => item?.State is TrackMatchState.Accepted or TrackMatchState.Pinned);
        var review = decisions.Count(item => item?.State is TrackMatchState.Suggested or TrackMatchState.Ambiguous);
        var rejected = decisions.Count(item => item?.State == TrackMatchState.Rejected);
        var unresolved = entries.Length - matched;
        var playableOutcomes = new HashSet<PlaylistEntryOutcome>
        {
            PlaylistEntryOutcome.Matched,
            PlaylistEntryOutcome.Reused,
            PlaylistEntryOutcome.Added,
            PlaylistEntryOutcome.Reordered
        };
        var playable = runEntries.Length == 0
            ? matched
            : runEntries.Count(item => playableOutcomes.Contains(item.Outcome));
        var materialized = runEntries.Count(item =>
            item.Outcome is PlaylistEntryOutcome.Reused or PlaylistEntryOutcome.Added or PlaylistEntryOutcome.Reordered);
        return new PlaylistListMetrics(
            entries.Length,
            matched,
            unresolved,
            review,
            rejected,
            playable,
            materialized,
            snapshot?.Id,
            snapshot?.SnapshotVersion,
            run?.Id,
            run?.Generation);
    }

    private static object ToListDto(PlaylistLinkRecord value, PlaylistSourceSnapshotRecord? snapshot, PlaylistSyncRunRecord? run, PlaylistListMetrics metrics) => new
    {
        id = value.Id,
        enabled = value.Enabled,
        name = snapshot?.Name ?? "Playlist",
        description = snapshot?.Description,
        artworkUrl = snapshot?.ArtworkReferenceKey == null ? null :
            $"/api/admin/playlist-sources/{value.ProviderAccountId}/playlists/{Uri.EscapeDataString(value.SourcePlaylistId)}/artwork",
        providerAccountId = value.ProviderAccountId,
        sourceProviderId = value.SourceProviderId,
        libraryScopeId = value.LibraryScopeId,
        targetProtocol = value.TargetProtocol,
        targetBackendInstanceId = value.TargetBackendInstanceId,
        mode = value.Mode.ToString().ToLowerInvariant(),
        materializationMode = value.MaterializationMode.ToString().ToLowerInvariant(),
        scheduleId = value.ScheduleId,
        targetPlaylistId = value.TargetPlaylistId,
        targetCredentialReferenceId = value.TargetCredentialReferenceId,
        mirrorStaleEntries = value.MirrorStaleEntries,
        preserveManualEntries = value.PreserveManualEntries,
        syncName = value.SyncName,
        syncDescription = value.SyncDescription,
        syncArtwork = value.SyncArtwork,
        ruleVersion = value.RuleVersion,
        policyVersion = value.PolicyVersion,
        revision = value.Revision,
        lastRunAt = run?.CompletedAt ?? run?.StartedAt,
        lastRunState = run?.State.ToString().ToLowerInvariant(),
        trackCount = metrics.Total,
        matchedCount = metrics.Matched,
        unmatchedCount = metrics.Unresolved,
        playableCount = metrics.Playable,
        materializedCount = metrics.Materialized,
        metrics = new
        {
            total = metrics.Total,
            matched = metrics.Matched,
            unresolved = metrics.Unresolved,
            review = metrics.Review,
            rejected = metrics.Rejected,
            playable = metrics.Playable,
            materialized = metrics.Materialized,
            snapshotId = metrics.SnapshotId,
            snapshotVersion = metrics.SnapshotVersion,
            runId = metrics.RunId,
            generation = metrics.Generation
        },
        virtualPlaylistId = PlaylistVirtualizationService.CreateProtocolId(value.Id)
    };
    private sealed record PlaylistListMetrics(
        int Total,
        int Matched,
        int Unresolved,
        int Review,
        int Rejected,
        int Playable,
        int Materialized,
        Guid? SnapshotId,
        int? SnapshotVersion,
        Guid? RunId,
        long? Generation);
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
public sealed record DeletePlaylistLinkRequest(long ExpectedRevision);
public sealed record SetPlaylistLinkStateRequest(long ExpectedRevision, bool Enabled);
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
