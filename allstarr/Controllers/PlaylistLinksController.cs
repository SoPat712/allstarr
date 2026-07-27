using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/playlist-links")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class PlaylistLinksController(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IPlaylistPersistenceService playlists,
    DurablePlaylistProjectionReader projections,
    ITrackMatchRepository matches,
    PlaylistOrchestrationService orchestration,
    DurableJobQueue jobs,
    EncryptedSecretStore secretStore,
    IProviderRegistry providerRegistry,
    IProviderRouter providerRouter,
    IBackendPlaylistTargetResolver targetResolver,
    IMediaAssetResolver mediaAssets,
    IApplicationCache applicationCache,
    IPlatformClock clock,
    ProviderPolicyOptions providerPolicy,
    AdminProtocolExecutionContextFactory protocolContexts,
    IConfiguration configuration) : ControllerBase
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
            var creatorIds = accounts
                .Select(item => item.CreatedByUserId ?? item.OwnerUserId)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .Distinct()
                .ToArray();
            var creatorNames = await db.Users.AsNoTracking()
                .Where(item => creatorIds.Contains(item.Id))
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
            var configuredProviderOrder = (configuration["Providers:PlaylistOrder"] ??
                                           configuration["MULTI_PROVIDER_PLAYLIST_ORDER"] ??
                                           "spotify,deezer,qobuz")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((id, index) => (id: id.ToLowerInvariant(), index))
                .GroupBy(item => item.id)
                .ToDictionary(group => group.Key, group => group.First().index);
            return Ok(new
            {
                accounts = availableAccounts.Select(item => ToPlaylistSourceAccountDto(
                    item,
                    true,
                    null,
                    (item.CreatedByUserId ?? item.OwnerUserId) is { } creatorId
                        ? creatorNames.GetValueOrDefault(creatorId)
                        : null,
                    item.Scope == ProviderAccountScope.Global &&
                    session.IsAdministrator &&
                    !providerPolicy.AllowGlobalPersonalAccounts)),
                blockedAccounts = blockedAccounts.Select(item => ToPlaylistSourceAccountDto(
                    item,
                    false,
                    "shared-playlist-credentials-disabled",
                    (item.CreatedByUserId ?? item.OwnerUserId) is { } creatorId
                        ? creatorNames.GetValueOrDefault(creatorId)
                        : null)),
                providers = supportedProviders.Values
                    .OrderBy(provider => configuredProviderOrder.GetValueOrDefault(provider.Id, int.MaxValue))
                    .ThenBy(provider => provider.Id, StringComparer.Ordinal)
                    .Select(provider =>
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
        [FromQuery] int limit = 100,
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

            var requestedCursor = cursor;
            var currentCursor = cursor;
            var items = new List<PlaylistDiscoveryItemCacheEntry>();
            var seenPlaylistIds = new HashSet<string>(StringComparer.Ordinal);
            string? nextCursor = null;
            string? snapshotVersion = null;
            var isPartial = false;
            const int maximumPages = 40;

            for (var pageNumber = 0; pageNumber < maximumPages; pageNumber++)
            {
                var pageRequest = new ProviderPageRequest(limit, currentCursor);
                var cacheKey = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
                    session.TenantId,
                    session.AllstarrUserId,
                    account.Id,
                    account.Revision,
                    providerId,
                    query,
                    currentCursor,
                    limit);
                var page = await applicationCache.GetAsync<PlaylistDiscoveryPageCacheEntry>(cacheKey);
                if (page == null)
                {
                    var outcome = string.IsNullOrWhiteSpace(query)
                        ? await candidate.Implementation.GetUserPlaylistsAsync(
                            candidate.Context,
                            new ProviderUserPlaylistsRequest(pageRequest))
                        : await candidate.Implementation.SearchPlaylistsAsync(
                            candidate.Context,
                            new ProviderPlaylistSearchRequest(query.Trim(), pageRequest));
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
                    page = ToDiscoveryCacheEntry(outcome.RequireValue());
                    await applicationCache.SetAsync(cacheKey, page);
                }

                foreach (var item in page.Items)
                {
                    if (seenPlaylistIds.Add(item.Id))
                    {
                        items.Add(item);
                    }
                }

                nextCursor = page.NextCursor;
                snapshotVersion = page.SnapshotVersion ?? snapshotVersion;
                isPartial = page.IsPartial;
                if (requestedCursor != null || !page.IsPartial || string.IsNullOrWhiteSpace(nextCursor))
                {
                    break;
                }
                currentCursor = nextCursor;
            }

            return Ok(new
            {
                providerId,
                accountId = account.Id,
                items = items.Select(item => ToPlaylistSummaryDto(item, account.Id)),
                nextCursor,
                isPartial,
                snapshotVersion
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
            ProviderError? failure = null;
            var asset = await mediaAssets.ResolveAsync(
                new MediaAssetIdentity(
                    session.TenantId,
                    session.AllstarrUserId,
                    account.Id,
                    providerId,
                    "playlist",
                    playlistId,
                    revision,
                    Width: 512),
                async token =>
                {
                    var outcome = await candidate.Implementation.ResolveArtworkAsync(
                        candidate.Context,
                        new ProviderPlaylistArtworkRequest(reference, maximumBytes: 4 * 1024 * 1024));
                    if (!outcome.IsSuccess)
                    {
                        failure = outcome.Error;
                        return null;
                    }
                    var artwork = outcome.RequireValue();
                    return new MediaAssetSource(artwork.Bytes, artwork.ContentType);
                },
                4 * 1024 * 1024,
                cancellationToken);
            if (asset == null)
                return NotFound(new { error = "Playlist artwork is unavailable", reasonCode = failure?.Code });
            Response.Headers.CacheControl = "private, max-age=300";
            return File(asset.Bytes, asset.ContentType);
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
            var backendPlaylistId = Required(playlistId, nameof(playlistId));
            var asset = await mediaAssets.ResolveAsync(
                new MediaAssetIdentity(
                    session.TenantId,
                    session.AllstarrUserId,
                    null,
                    protocol,
                    "playlist",
                    $"{identity.BackendInstanceId}:{backendPlaylistId}",
                    artworkReference,
                    Width: 512),
                async token =>
                {
                    var result = await targetResolver.Resolve(protocol).ReadArtworkAsync(
                        context,
                        backendPlaylistId,
                        artworkReference,
                        token);
                    return result.IsSuccess && result.Value != null
                        ? new MediaAssetSource(result.Value.Bytes, result.Value.ContentType)
                        : null;
                },
                4 * 1024 * 1024,
                cancellationToken);
            if (asset == null) return NotFound();
            Response.Headers.CacheControl = "private, max-age=300";
            return File(asset.Bytes, asset.ContentType);
        });
    }
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? libraryScopeId, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var context = await CreateExecutionAsync(session, libraryScopeId, cancellationToken);
            var links = await playlists.ListLinksAsync(context, libraryScopeId, cancellationToken);
            var projectionsByLink = await projections.ReadByLinkIdsAsync(
                session.TenantId!.Value,
                session.IsAdministrator ? null : session.AllstarrUserId,
                links.Select(item => item.Id).ToArray(),
                cancellationToken);
            return Ok(new
            {
                playlistLinks = links.Select(link =>
                    ToListDto(link, projectionsByLink.GetValueOrDefault(link.Id)))
            });
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            await LoadScopedLink(session, id, cancellationToken);
            var projection = await projections.ReadByLinkIdAsync(
                session.TenantId!.Value,
                session.IsAdministrator ? null : session.AllstarrUserId,
                id,
                cancellationToken);
            return projection == null ? NotFound() : Ok(ToProjectionDto(projection));
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
            var snapshot = await matches.FindSnapshotAsync(
                session.TenantId!.Value,
                externalSnapshotId,
                cancellationToken) ?? throw new KeyNotFoundException("External snapshot not found.");
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
            var value = await matches.FindOverrideAsync(
                session.TenantId!.Value,
                overrideId,
                cancellationToken) ?? throw new KeyNotFoundException("Override not found.");
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
        => await protocolContexts.CreateAsync(
            session, libraryScopeId, HttpContext.TraceIdentifier, cancellationToken);

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
    private static PlaylistDiscoveryPageCacheEntry ToDiscoveryCacheEntry(
        ProviderPage<ProviderPlaylistSummary> page) => new(
        page.Items.Select(value => new PlaylistDiscoveryItemCacheEntry(
            value.Id.Value,
            value.Id.ProviderId,
            value.Id.Catalog,
            value.Name,
            value.Description,
            value.Owner.DisplayName ?? value.Owner.ProviderUserId,
            value.TrackCount,
            value.SourceRevision,
            value.SourceETag,
            value.Artwork != null,
            value.Artwork?.ResourceId?.ProviderId,
            value.Artwork?.ResourceId?.Value,
            value.Artwork?.Revision)).ToArray(),
        page.NextCursor,
        page.IsPartial,
        page.SnapshotVersion);

    private static object ToPlaylistSummaryDto(PlaylistDiscoveryItemCacheEntry value, Guid accountId) => new
    {
        id = value.Id,
        providerId = value.ProviderId,
        catalog = value.Catalog,
        name = value.Name,
        description = value.Description,
        owner = value.Owner,
        trackCount = value.TrackCount,
        sourceRevision = value.SourceRevision,
        sourceETag = value.SourceETag,
        artworkUrl = !value.HasArtwork
            ? null
            : $"/api/admin/playlist-sources/{accountId}/playlists/{Uri.EscapeDataString(value.Id)}/artwork?revision={Uri.EscapeDataString(value.ArtworkRevision ?? value.SourceRevision)}",
        artworkReference = value.ArtworkResourceId == null ? null : new
        {
            providerId = value.ArtworkProviderId,
            id = value.ArtworkResourceId,
            revision = value.ArtworkRevision
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
    private static object ToListDto(PlaylistLinkRecord value, DurablePlaylistProjection? projection) => new
    {
        id = value.Id,
        enabled = value.Enabled,
        name = projection?.Name ?? "Playlist",
        description = projection?.Description,
        artworkUrl = projection?.ArtworkReferenceKey == null ? null :
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
        lastRunAt = projection?.CompletedAt,
        lastRunState = projection?.SyncState?.ToString().ToLowerInvariant(),
        materializationVerification = projection?.VerificationCode == null ? null : new
        {
            code = projection.VerificationCode,
            plannedTrackCount = projection.PlannedTargetTrackCount,
            plannedDurationMs = projection.PlannedTargetDurationMilliseconds,
            reportedTrackCount = projection.VerifiedTargetTrackCount,
            reportedDurationMs = projection.VerifiedTargetDurationMilliseconds,
            verifiedAt = projection.VerifiedAt
        },
        trackCount = projection?.TotalCount ?? 0,
        matchedCount = projection?.MatchedCount ?? 0,
        unmatchedCount = projection?.MissingCount ?? 0,
        playableCount = projection?.PlayableCount ?? 0,
        materializedCount = projection?.MaterializedCount ?? 0,
        routeCoverage = projection?.RouteCounts.Select(item => new
        {
            providerId = item.Key,
            count = item.Value
        }).ToArray() ?? [],
        metrics = new
        {
            total = projection?.TotalCount ?? 0,
            matched = projection?.MatchedCount ?? 0,
            unresolved = projection?.MissingCount ?? 0,
            review = projection?.ReviewCount ?? 0,
            rejected = projection?.RejectedCount ?? 0,
            playable = projection?.PlayableCount ?? 0,
            materialized = projection?.MaterializedCount ?? 0,
            snapshotId = projection?.SnapshotId,
            snapshotVersion = projection?.SnapshotVersion,
            runId = projection?.RunId,
            generation = projection?.Generation
        },
        virtualPlaylistId = PlaylistVirtualizationService.CreateProtocolId(value.Id)
    };
    private static object ToProjectionDto(DurablePlaylistProjection value) => new
    {
        id = value.LinkId,
        snapshotId = value.SnapshotId,
        snapshotVersion = value.SnapshotVersion,
        name = value.Name,
        sourceProviderId = value.SourceProviderId,
        targetProtocol = value.TargetProtocol,
        targetPlaylistId = value.TargetPlaylistId,
        artworkUrl = value.ArtworkReferenceKey == null ? null :
            $"/api/admin/playlist-sources/{value.ProviderAccountId}/playlists/{Uri.EscapeDataString(value.SourcePlaylistId)}/artwork",
        retrievedAt = value.RetrievedAt,
        completedAt = value.CompletedAt,
        syncState = value.SyncState?.ToString().ToLowerInvariant(),
        trackCount = value.TotalCount,
        localCount = value.LocalCount,
        externalCount = value.ExternalCount,
        unresolvedCount = value.MissingCount,
        routeCoverage = value.RouteCounts.Select(item => new
        {
            providerId = item.Key,
            count = item.Value
        }),
        durationMs = value.DurationMilliseconds,
        unknownDurationCount = value.UnknownDurationCount,
        materializationVerification = value.VerificationCode == null ? null : new
        {
            code = value.VerificationCode,
            plannedTrackCount = value.PlannedTargetTrackCount,
            plannedDurationMs = value.PlannedTargetDurationMilliseconds,
            reportedTrackCount = value.VerifiedTargetTrackCount,
            reportedDurationMs = value.VerifiedTargetDurationMilliseconds,
            verifiedAt = value.VerifiedAt
        },
        tracks = value.Entries.Select(item => new
        {
            position = item.Position,
            externalSnapshotId = item.ExternalSnapshotId,
            title = item.Title,
            artists = item.Artists,
            album = item.Album,
            isrc = item.Isrc,
            durationMs = item.DurationMilliseconds,
            durationProvenance = item.DurationProvenance,
            durationRetrievedAt = item.DurationRetrievedAt,
            artworkUrl = item.BackendItemId != null
                ? $"/api/admin/downloads/artwork/{Uri.EscapeDataString(item.BackendItemId)}"
                : item.RouteKind == "external" && item.RouteProviderId != null
                    ? $"/api/admin/downloads/artwork/{Uri.EscapeDataString($"ext-{item.RouteProviderId}-song-{item.ExternalId}")}"
                    : null,
            backendItemId = item.BackendItemId,
            routeKind = item.RouteKind,
            routeProviderId = item.RouteProviderId,
            matchState = item.MatchState?.ToString().ToLowerInvariant(),
            providerRoutes = item.ProviderRoutes.Select(route => new
            {
                providerId = route.ProviderId,
                externalId = route.ExternalId,
                pinned = route.IsManual
            })
        })
    };
    private sealed record PlaylistDiscoveryPageCacheEntry(
        IReadOnlyList<PlaylistDiscoveryItemCacheEntry> Items,
        string? NextCursor,
        bool IsPartial,
        string? SnapshotVersion);
    private sealed record PlaylistDiscoveryItemCacheEntry(
        string Id,
        string ProviderId,
        string? Catalog,
        string Name,
        string? Description,
        string Owner,
        int? TrackCount,
        string SourceRevision,
        string? SourceETag,
        bool HasArtwork,
        string? ArtworkProviderId,
        string? ArtworkResourceId,
        string? ArtworkRevision);
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
