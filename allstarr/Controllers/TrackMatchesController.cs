using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Models.Spotify;
using allstarr.Services.Admin;
using allstarr.Services.Spotify;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

/// <summary>
/// Provider-neutral review projection over durable Phase 4 match decisions.
/// Legacy Spotify mapping endpoints remain compatibility-only and are not consulted here.
/// </summary>
[ApiController]
[Route("api/admin/track-matches")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class TrackMatchesController(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    SpotifyMappingService spotifyMappings,
    SpotifyTrackMatchingService matchingService) : ControllerBase
{
    public sealed record ResolveTrackMatchRequest(
        string TargetType,
        Guid? LibraryTrackId = null,
        string? ExternalProvider = null,
        string? ExternalId = null,
        string? Reason = null);

    [HttpGet("spotify/{spotifyId}")]
    public async Task<IActionResult> Detail(
        string spotifyId,
        [FromQuery] string? backendItemId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        spotifyId = spotifyId.Trim();
        if (spotifyId.Length is < 3 or > 128) return BadRequest(new { error = "Spotify track id is invalid" });
        backendItemId = string.IsNullOrWhiteSpace(backendItemId) ? null : backendItemId.Trim();
        if (backendItemId?.Length > 256) return BadRequest(new { error = "Backend item id is invalid" });

        var legacy = await spotifyMappings.GetMappingAsync(spotifyId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;

        var spotifyIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.ProviderId == "spotify" && item.ExternalId == spotifyId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        var canonicalIds = spotifyIdentities.Select(item => item.CanonicalRecordingId).Distinct().ToArray();

        var identities = canonicalIds.Length == 0
            ? []
            : await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => item.TenantId == tenantId && canonicalIds.Contains(item.CanonicalRecordingId))
                .OrderBy(item => item.ProviderId).ThenBy(item => item.CreatedAt)
                .ToListAsync(cancellationToken);

        var localQuery = db.LibraryTracks.AsNoTracking().Where(item => item.TenantId == tenantId);
        if (!session.IsAdministrator) localQuery = localQuery.Where(item => item.OwnerUserId == userId);
        var localTracks = await localQuery
            .Where(item => (item.CanonicalRecordingId.HasValue && canonicalIds.Contains(item.CanonicalRecordingId.Value)) ||
                           (legacy != null && legacy.LocalId != null && item.BackendItemId == legacy.LocalId) ||
                           (backendItemId != null && item.BackendItemId == backendItemId))
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);

        var identityIds = identities.Select(item => item.Id).Concat(spotifyIdentities.Select(item => item.Id)).Distinct().ToArray();
        var snapshotQuery = db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value));
        if (!session.IsAdministrator) snapshotQuery = snapshotQuery.Where(item => item.OwnerUserId == userId);
        var snapshots = await snapshotQuery.OrderByDescending(item => item.RetrievedAt).ToListAsync(cancellationToken);
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisions = snapshotIds.Length == 0
            ? []
            : await db.TrackMatches.AsNoTracking()
                .Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.DecidedAt).ToListAsync(cancellationToken);
        var overrides = snapshotIds.Length == 0
            ? []
            : await db.ManualTrackOverrides.AsNoTracking()
                .Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.CreatedAt).ToListAsync(cancellationToken);

        var externalIds = identities.Select(item => item.ExternalId)
            .Concat(legacy?.ExternalMappings.Select(item => item.ExternalId) ?? [])
            .Append(spotifyId).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().ToArray();
        var artifactQuery = db.ProviderDownloadArtifacts.AsNoTracking()
            .Where(item => item.TenantId == tenantId && externalIds.Contains(item.ProviderArtifactId));
        if (!session.IsAdministrator) artifactQuery = artifactQuery.Where(item => item.OwnerUserId == null || item.OwnerUserId == userId);
        var artifacts = await artifactQuery.OrderByDescending(item => item.CreatedAt).Take(50).ToListAsync(cancellationToken);

        var firstMappedAt = new DateTimeOffset?[]
        {
            legacy?.CreatedAt == default ? null : new DateTimeOffset(legacy!.CreatedAt),
            identities.Count == 0 ? null : identities.Min(item => item.CreatedAt),
            decisions.Count == 0 ? null : decisions.Min(item => item.DecidedAt),
            overrides.Count == 0 ? null : overrides.Min(item => item.CreatedAt)
        }.Where(item => item.HasValue).Min();
        var lastMappedAt = new DateTimeOffset?[]
        {
            legacy?.UpdatedAt is null ? null : new DateTimeOffset(legacy.UpdatedAt.Value),
            legacy?.LastValidatedAt is null ? null : new DateTimeOffset(legacy.LastValidatedAt.Value),
            identities.Count == 0 ? null : identities.Max(item => item.UpdatedAt),
            decisions.Count == 0 ? null : decisions.Max(item => item.DecidedAt),
            overrides.Count == 0 ? null : overrides.Max(item => item.CreatedAt)
        }.Where(item => item.HasValue).Max();

        var activity = new List<object>();
        if (legacy != null)
        {
            activity.Add(new { at = legacy.CreatedAt, kind = "mapping", title = "Legacy mapping created", detail = $"{legacy.Source} · {legacy.TargetType}" });
            if (legacy.LastValidatedAt.HasValue) activity.Add(new { at = legacy.LastValidatedAt.Value, kind = "validation", title = "Legacy mapping validated", detail = legacy.TargetType });
        }
        activity.AddRange(identities.Select(item => (object)new { at = item.VerifiedAt, kind = "identity", title = $"{item.ProviderId} identity verified", detail = item.VerificationMethod }));
        activity.AddRange(snapshots.Select(item => (object)new { at = item.RetrievedAt, kind = "cache", title = $"{item.ProviderId} metadata cached", detail = $"Snapshot v{item.SnapshotVersion}" }));
        activity.AddRange(decisions.Select(item => (object)new { at = item.DecidedAt, kind = "match", title = $"Match {item.State.ToString().ToLowerInvariant()}", detail = $"{item.Confidence:P0} confidence · {item.PolicyVersion}" }));
        activity.AddRange(overrides.Select(item => (object)new { at = item.CreatedAt, kind = "override", title = $"Manual {item.Decision.ToString().ToLowerInvariant()}", detail = item.Reason }));
        activity.AddRange(artifacts.Select(item => (object)new { at = item.PlacedAt ?? item.VerifiedAt, kind = "download", title = $"{item.ProviderId} audio {item.State.ToString().ToLowerInvariant()}", detail = $"{item.Length} bytes" }));

        var matchHistory = decisions.Select(item => (object)new
        {
            item.Id,
            state = item.State.ToString().ToLowerInvariant(),
            item.Confidence,
            item.Threshold,
            item.DecisionVersion,
            item.PolicyVersion,
            source = "durable matcher",
            reasons = ParseArray(item.ReasonsJson),
            warnings = ParseArray(item.WarningsJson),
            item.CorrelationId,
            item.DecidedAt
        }).ToList();
        if (legacy != null)
        {
            var route = legacy.TargetType.Equals("local", StringComparison.OrdinalIgnoreCase)
                ? "Jellyfin library"
                : legacy.TryGetExternalTarget(null, out var provider, out _)
                    ? provider
                    : "external provider";
            matchHistory.Add(new
            {
                id = $"legacy-{spotifyId}",
                state = legacy.LastValidatedAt.HasValue ? "accepted" : "candidate",
                confidence = (double?)null,
                threshold = (double?)null,
                decisionVersion = (int?)null,
                policyVersion = "compatibility-v2",
                source = legacy.Source.Equals("manual", StringComparison.OrdinalIgnoreCase)
                    ? "manual compatibility mapping"
                    : "legacy matcher",
                reasons = new[] { $"Compatibility matching selected {route}." },
                warnings = legacy.LastValidatedAt.HasValue ? Array.Empty<string>() : new[] { "Awaiting validation." },
                correlationId = (string?)null,
                decidedAt = (DateTimeOffset?)(legacy.UpdatedAt ?? legacy.CreatedAt)
            });
        }
        else if (backendItemId != null && localTracks.Count == 0)
        {
            matchHistory.Add(new
            {
                id = $"materialized-{backendItemId}",
                state = "accepted",
                confidence = (double?)null,
                threshold = (double?)null,
                decisionVersion = (int?)null,
                policyVersion = "materialized-playlist",
                source = "materialized Jellyfin playlist",
                reasons = new[] { "The current Jellyfin playlist contains this backend item." },
                warnings = new[] { "Durable library indexing is still pending for this item." },
                correlationId = (string?)null,
                decidedAt = (DateTimeOffset?)null
            });
        }

        var primaryLocal = localTracks.FirstOrDefault();
        return Ok(new
        {
            spotifyId,
            found = legacy != null || identities.Count > 0 || decisions.Count > 0 || localTracks.Count > 0,
            firstMappedAt,
            lastMappedAt,
            durationMilliseconds = primaryLocal?.DurationMilliseconds ?? legacy?.Metadata?.DurationMs,
            metadata = new
            {
                title = primaryLocal?.Title ?? legacy?.Metadata?.Title,
                artist = primaryLocal?.Artist ?? legacy?.Metadata?.Artist,
                album = primaryLocal?.Album ?? legacy?.Metadata?.Album,
                artworkUrl = legacy?.Metadata?.ArtworkUrl,
                isrc = primaryLocal?.Isrc,
                musicBrainzRecordingId = primaryLocal?.MusicBrainzRecordingId
            },
            legacyMapping = legacy == null ? null : new
            {
                origin = "legacy-cache",
                legacy.TargetType,
                legacy.LocalId,
                legacy.Source,
                legacy.CreatedAt,
                legacy.UpdatedAt,
                legacy.LastValidatedAt,
                externalMappings = legacy.ExternalMappings.Select(item => new { item.Provider, item.ExternalId, item.Source, item.CreatedAt, item.UpdatedAt })
            },
            providerIdentities = identities.Concat(spotifyIdentities).DistinctBy(item => item.Id).Select(item => new
            {
                item.Id,
                item.CanonicalRecordingId,
                item.ProviderId,
                item.ExternalId,
                resourceKind = item.ResourceKind.ToString().ToLowerInvariant(),
                scope = item.Scope.ToString().ToLowerInvariant(),
                verification = item.Verification.ToString().ToLowerInvariant(),
                item.VerificationMethod,
                item.DecisionVersion,
                item.CreatedAt,
                item.VerifiedAt,
                item.UpdatedAt
            }),
            localTracks = localTracks.Select(item => new
            {
                item.Id,
                item.CanonicalRecordingId,
                item.BackendItemId,
                item.LibraryScopeId,
                item.Title,
                item.Artist,
                item.Album,
                item.AlbumArtist,
                item.DurationMilliseconds,
                item.Isrc,
                item.MusicBrainzRecordingId,
                providerIds = ParseObject(item.ProviderIdsJson),
                item.IndexedAt,
                item.SourceModifiedAt,
                item.UpdatedAt
            }),
            matchHistory,
            overrides = overrides.Select(item => new { item.Id, decision = item.Decision.ToString().ToLowerInvariant(), item.Reason, item.DecisionVersion, item.CreatedAt, item.RevokedAt }),
            cache = new
            {
                lastMetadataCachedAt = snapshots.FirstOrDefault()?.RetrievedAt,
                lastAudioCachedAt = artifacts.FirstOrDefault()?.CreatedAt,
                artifacts = artifacts.Select(item => new { item.ProviderId, state = item.State.ToString().ToLowerInvariant(), item.Length, item.CreatedAt, item.VerifiedAt, item.PlacedAt })
            },
            activity = activity.OrderByDescending(item => ActivityAt(item)).Take(100)
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? libraryScopeId = null,
        [FromQuery] string? state = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (page < 1 || pageSize is < 1 or > 200) return BadRequest(new { error = "Page and pageSize are outside the supported range" });
        if (!string.IsNullOrWhiteSpace(state) &&
            !state.Equals("attention", StringComparison.OrdinalIgnoreCase) &&
            !state.Equals("matched", StringComparison.OrdinalIgnoreCase) &&
            !Enum.TryParse<TrackMatchState>(state, true, out _))
            return BadRequest(new { error = "State is not a valid match state" });

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var snapshotsQuery = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.TenantId == tenantId);
        if (!session.IsAdministrator) snapshotsQuery = snapshotsQuery.Where(item => item.OwnerUserId == userId);
        if (!string.IsNullOrWhiteSpace(libraryScopeId)) snapshotsQuery = snapshotsQuery.Where(item => item.LibraryScopeId == libraryScopeId.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            snapshotsQuery = snapshotsQuery.Where(item =>
                EF.Functions.ILike(item.ProviderId, pattern, "\\") ||
                EF.Functions.ILike(item.PayloadJson, pattern, "\\"));
        }
        var snapshots = await snapshotsQuery.OrderByDescending(item => item.RetrievedAt).Take(5000).ToListAsync(cancellationToken);
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisionQuery = db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId));
        var latestDecisionVersions = decisionQuery
            .GroupBy(item => item.ExternalSnapshotId)
            .Select(group => new
            {
                ExternalSnapshotId = group.Key,
                DecisionVersion = group.Max(item => item.DecisionVersion)
            });
        var decisions = (await decisionQuery.Join(
                latestDecisionVersions,
                item => new { item.ExternalSnapshotId, item.DecisionVersion },
                latest => new { latest.ExternalSnapshotId, latest.DecisionVersion },
                (item, _) => item)
            .ToListAsync(cancellationToken))
            .ToDictionary(item => item.ExternalSnapshotId);
        var overrides = (await db.ManualTrackOverrides.AsNoTracking().Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId) && item.RevokedAt == null)
                .ToListAsync(cancellationToken)).ToDictionary(item => item.ExternalSnapshotId);
        var libraryIds = decisions.Values.Where(item => item.LibraryTrackId.HasValue).Select(item => item.LibraryTrackId!.Value)
            .Concat(overrides.Values.Where(item => item.LibraryTrackId.HasValue).Select(item => item.LibraryTrackId!.Value)).Distinct().ToArray();
        var library = await db.LibraryTracks.AsNoTracking().Where(item => item.TenantId == tenantId && libraryIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var canonicalIds = decisions.Values.Where(item => item.CanonicalRecordingId.HasValue).Select(item => item.CanonicalRecordingId!.Value)
            .Concat(library.Values.Where(item => item.CanonicalRecordingId.HasValue).Select(item => item.CanonicalRecordingId!.Value)).Distinct().ToArray();
        var identities = (await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.TenantId == tenantId && canonicalIds.Contains(item.CanonicalRecordingId))
                .OrderBy(item => item.ProviderId).ThenBy(item => item.ExternalId).ToListAsync(cancellationToken))
            .GroupBy(item => item.CanonicalRecordingId).ToDictionary(group => group.Key, group => group.ToArray());

        var allRows = snapshots.Select(snapshot => Row(snapshot, decisions.GetValueOrDefault(snapshot.Id),
                overrides.GetValueOrDefault(snapshot.Id), library, identities))
            .ToArray();
        var rows = allRows.Where(row => MatchesStateFilter(row.State, state)).ToArray();
        var total = rows.Length;
        var items = rows.Skip((page - 1) * pageSize).Take(pageSize).Select(row => row.Value).ToArray();
        return Ok(new
        {
            matches = items,
            stats = new
            {
                total = allRows.Length,
                accepted = allRows.Count(item => item.State is TrackMatchState.Accepted or TrackMatchState.Pinned),
                unresolved = allRows.Count(item => item.State == TrackMatchState.Unresolved),
                review = allRows.Count(item => item.State is TrackMatchState.Suggested or TrackMatchState.Ambiguous)
            },
            pagination = new { page, pageSize, total, totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) }
        }
        );
    }

    [HttpGet("targets/local")]
    public async Task<IActionResult> SearchLocalTargets(
        [FromQuery] string query,
        [FromQuery] string? libraryScopeId = null,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        query = query?.Trim() ?? string.Empty;
        if (query.Length < 2) return BadRequest(new { error = "Enter at least two characters" });
        limit = Math.Clamp(limit, 1, 50);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var pattern = $"%{query.Replace("%", "\\%").Replace("_", "\\_")}%";
        var tracks = db.LibraryTracks.AsNoTracking().Where(item => item.TenantId == tenantId);
        if (!session.IsAdministrator) tracks = tracks.Where(item => item.OwnerUserId == userId);
        if (!string.IsNullOrWhiteSpace(libraryScopeId)) tracks = tracks.Where(item => item.LibraryScopeId == libraryScopeId.Trim());
        tracks = tracks.Where(item =>
            EF.Functions.ILike(item.Title, pattern, "\\") ||
            EF.Functions.ILike(item.Artist, pattern, "\\") ||
            (item.Album != null && EF.Functions.ILike(item.Album, pattern, "\\")));
        var values = await tracks.OrderBy(item => item.Artist).ThenBy(item => item.Title).Take(limit)
            .Select(item => new
            {
                item.Id,
                item.BackendItemId,
                item.Title,
                item.Artist,
                item.Album,
                item.DurationMilliseconds,
                item.Isrc,
                item.CoverArtReference
            }).ToArrayAsync(cancellationToken);
        return Ok(new { tracks = values });
    }

    [HttpPost("{externalSnapshotId:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid externalSnapshotId,
        [FromBody] ResolveTrackMatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        var targetType = request.TargetType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (targetType is not ("local" or "provider" or "reject"))
            return BadRequest(new { error = "TargetType must be local, provider, or reject" });

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId && item.TenantId == tenantId, cancellationToken);
        if (snapshot == null) return NotFound();
        if (!session.IsAdministrator && snapshot.OwnerUserId != userId) return Forbid();

        var sourceIdentity = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.SingleOrDefaultAsync(
                item => item.Id == snapshot.ProviderTrackIdentityId.Value && item.TenantId == tenantId,
                cancellationToken)
            : null;
        var latestDecision = await db.TrackMatches.Where(item =>
                item.TenantId == tenantId && item.ExternalSnapshotId == externalSnapshotId)
            .OrderByDescending(item => item.DecisionVersion).FirstOrDefaultAsync(cancellationToken);
        var decisionVersion = (latestDecision?.DecisionVersion ?? 0) + 1;
        var now = DateTimeOffset.UtcNow;
        var activeOverride = await db.ManualTrackOverrides.SingleOrDefaultAsync(item =>
            item.TenantId == tenantId && item.ExternalSnapshotId == externalSnapshotId && item.RevokedAt == null,
            cancellationToken);
        if (activeOverride != null)
        {
            activeOverride.RevokedAt = now;
            activeOverride.Revision++;
        }

        LibraryTrackRecord? localTrack = null;
        string? providerId = null;
        string? externalId = null;
        if (targetType == "local")
        {
            if (!request.LibraryTrackId.HasValue) return BadRequest(new { error = "LibraryTrackId is required for a local match" });
            localTrack = await db.LibraryTracks.SingleOrDefaultAsync(item =>
                item.Id == request.LibraryTrackId.Value && item.TenantId == tenantId &&
                item.LibraryScopeId == snapshot.LibraryScopeId, cancellationToken);
            if (localTrack == null || (!session.IsAdministrator && localTrack.OwnerUserId != userId)) return NotFound();
            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = snapshot.OwnerUserId,
                ExternalSnapshotId = snapshot.Id,
                LibraryTrackId = localTrack.Id,
                LibraryScopeId = snapshot.LibraryScopeId,
                Decision = ManualOverrideDecision.Pin,
                Reason = CleanReason(request.Reason, "Selected from the indexed local library"),
                DecisionVersion = decisionVersion,
                CreatedAt = now
            });
        }
        else if (targetType == "reject")
        {
            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = snapshot.OwnerUserId,
                ExternalSnapshotId = snapshot.Id,
                LibraryScopeId = snapshot.LibraryScopeId,
                Decision = ManualOverrideDecision.Reject,
                Reason = CleanReason(request.Reason, "Rejected during manual review"),
                DecisionVersion = decisionVersion,
                CreatedAt = now
            });
        }
        else
        {
            providerId = request.ExternalProvider?.Trim().ToLowerInvariant();
            externalId = request.ExternalId?.Trim();
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(externalId))
                return BadRequest(new { error = "ExternalProvider and ExternalId are required for a provider match" });
            if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(providerId))
                return BadRequest(new { error = "That provider cannot supply playback audio" });
            var canonicalId = latestDecision?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
            if (!canonicalId.HasValue) return Conflict(new { error = "The source track has no canonical identity yet; rematch it first" });

            var externalHash = Hash(externalId);
            var identity = await db.ProviderTrackIdentities.SingleOrDefaultAsync(item =>
                item.TenantId == tenantId && item.ProviderId == providerId && item.ResourceKind == ProviderResourceKind.Track &&
                item.CatalogNamespace == "default" && item.Scope == ProviderIdentityScope.Catalog &&
                item.ExternalIdHash == externalHash, cancellationToken);
            if (identity != null && identity.CanonicalRecordingId != canonicalId.Value)
                return Conflict(new { error = "That provider track is already linked to a different recording" });
            if (identity == null)
            {
                db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    CanonicalRecordingId = canonicalId.Value,
                    ProviderId = providerId,
                    ResourceKind = ProviderResourceKind.Track,
                    CatalogNamespace = "default",
                    Scope = ProviderIdentityScope.Catalog,
                    ExternalId = externalId,
                    ExternalIdHash = externalHash,
                    Verification = ProviderIdentityVerification.Pinned,
                    VerificationMethod = "manual-review",
                    DecisionVersion = decisionVersion,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = snapshot.OwnerUserId,
                ExternalSnapshotId = snapshot.Id,
                CanonicalRecordingId = canonicalId.Value,
                LibraryScopeId = snapshot.LibraryScopeId,
                State = TrackMatchState.Suggested,
                Confidence = 1,
                Threshold = 1,
                DecisionVersion = decisionVersion,
                PolicyVersion = "manual-provider-route-v1",
                CandidateResultsJson = "[]",
                ReasonsJson = JsonSerializer.Serialize(new[] { $"Manually selected {providerId} playback route" }),
                WarningsJson = "[]",
                CorrelationId = HttpContext.TraceIdentifier,
                DecidedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        var metadata = Metadata(snapshot.PayloadJson);
        var compatibilityUpdated = true;
        if (sourceIdentity?.ProviderId.Equals("spotify", StringComparison.OrdinalIgnoreCase) == true)
        {
            compatibilityUpdated = targetType switch
            {
                "local" => await spotifyMappings.SaveManualMappingAsync(sourceIdentity.ExternalId, "local",
                    localTrack!.BackendItemId, metadata: ToTrackMetadata(metadata)),
                "provider" => await spotifyMappings.SaveManualMappingAsync(sourceIdentity.ExternalId, "external",
                    externalProvider: providerId, externalId: externalId, metadata: ToTrackMetadata(metadata)),
                _ => await spotifyMappings.DeleteMappingAsync(sourceIdentity.ExternalId)
            };
        }
        return Ok(new { success = true, compatibilityUpdated });
    }

    [HttpPost("{externalSnapshotId:guid}/rematch")]
    public async Task<IActionResult> Rematch(Guid externalSnapshotId, CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId && item.TenantId == tenantId, cancellationToken);
        if (snapshot == null) return NotFound();
        if (!session.IsAdministrator && snapshot.OwnerUserId != userId) return Forbid();
        var active = await db.ManualTrackOverrides.SingleOrDefaultAsync(item =>
            item.TenantId == tenantId && item.ExternalSnapshotId == snapshot.Id && item.RevokedAt == null,
            cancellationToken);
        if (active != null)
        {
            active.RevokedAt = DateTimeOffset.UtcNow;
            active.Revision++;
            await db.SaveChangesAsync(cancellationToken);
        }
        var source = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == snapshot.ProviderTrackIdentityId.Value && item.TenantId == tenantId,
                cancellationToken)
            : null;
        if (source?.ProviderId.Equals("spotify", StringComparison.OrdinalIgnoreCase) == true)
        {
            await spotifyMappings.DeleteMappingAsync(source.ExternalId);
            await matchingService.TriggerMatchingAsync(cancellationToken);
        }
        return Accepted(new { queued = true });
    }

    private static bool MatchesStateFilter(TrackMatchState state, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        (filter.Equals("attention", StringComparison.OrdinalIgnoreCase) && state is TrackMatchState.Unresolved or TrackMatchState.Suggested or TrackMatchState.Ambiguous or TrackMatchState.Rejected) ||
        (filter.Equals("matched", StringComparison.OrdinalIgnoreCase) && state is TrackMatchState.Accepted or TrackMatchState.Pinned) ||
        state.ToString().Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static string CleanReason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static TrackMetadata ToTrackMetadata((string? Title, string? Artist, string? Album) value) => new()
    {
        Title = value.Title,
        Artist = value.Artist,
        Album = value.Album
    };

    private static MatchRow Row(ExternalMetadataSnapshotRecord snapshot, TrackMatchRecord? decision,
        ManualTrackOverrideRecord? manual, IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord[]> identities)
    {
        var state = manual?.Decision == ManualOverrideDecision.Pin ? TrackMatchState.Pinned :
            manual?.Decision == ManualOverrideDecision.Reject ? TrackMatchState.Rejected : decision?.State ?? TrackMatchState.Unresolved;
        var trackId = manual?.LibraryTrackId ?? decision?.LibraryTrackId;
        library.TryGetValue(trackId ?? Guid.Empty, out var track);
        var canonicalId = decision?.CanonicalRecordingId ?? track?.CanonicalRecordingId;
        var providerIdentities = canonicalId.HasValue && identities.TryGetValue(canonicalId.Value, out var values)
            ? values.Select(item => new { item.ProviderId, item.ExternalId, scope = item.Scope.ToString(), verification = item.Verification.ToString() }).ToArray()
            : [];
        var metadata = Metadata(snapshot.PayloadJson);
        var value = new
        {
            externalSnapshotId = snapshot.Id,
            snapshot.ProviderId,
            snapshot.ProviderAccountId,
            snapshot.LibraryScopeId,
            state = state.ToString().ToLowerInvariant(),
            decision?.Confidence,
            decision?.Threshold,
            decision?.DecisionVersion,
            canonicalRecordingId = canonicalId,
            libraryTrackId = trackId,
            overrideId = manual?.Id,
            overrideRevision = manual?.Revision,
            title = metadata.Title,
            artist = metadata.Artist,
            album = metadata.Album,
            localTrack = track == null ? null : new { track.Id, track.BackendItemId, track.Title, track.Artist, track.Album },
            providerIdentities,
            candidates = ParseCandidates(decision?.CandidateResultsJson),
            reasons = ParseArray(decision?.ReasonsJson),
            warnings = ParseArray(decision?.WarningsJson),
            decidedAt = decision?.DecidedAt,
            reviewedAt = manual?.CreatedAt
        };
        return new(state, $"{metadata.Title} {metadata.Artist} {metadata.Album} {snapshot.ProviderId} {track?.Title} {track?.Artist}", value);
    }

    private static (string? Title, string? Artist, string? Album) Metadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = Text(root, "title") ?? Text(root, "Title") ?? Text(root, "name") ?? Text(root, "Name");
            var artist = Text(root, "artist") ?? Text(root, "Artist");
            if (artist == null && (root.TryGetProperty("artists", out var artists) || root.TryGetProperty("Artists", out artists)) && artists.ValueKind == JsonValueKind.Array)
                artist = string.Join(", ", artists.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : Text(item, "name")).Where(item => item != null));
            return (title, artist, Text(root, "album") ?? Text(root, "Album") ?? Text(root, "albumTitle") ?? Text(root, "AlbumTitle"));
        }
        catch (JsonException) { return (null, null, null); }
    }

    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] ParseArray(string? json)
    { try { return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; } catch (JsonException) { return []; } }

    private static object ParseObject(string? json)
    { try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? "{}") ?? []; } catch (JsonException) { return new Dictionary<string, string>(); } }

    private static DateTimeOffset ActivityAt(object value)
    {
        var property = value.GetType().GetProperty("at");
        var raw = property?.GetValue(value);
        return raw switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(dateTime),
            _ => DateTimeOffset.MinValue
        };
    }

    private static object[] ParseCandidates(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray().Select(item => new
            {
                libraryTrackId = Text(item, "libraryTrackId") ?? Text(item, "LibraryTrackId"),
                backendItemId = Text(item, "backendItemId") ?? Text(item, "BackendItemId"),
                confidence = Number(item, "confidence") ?? Number(item, "Confidence")
            }).Where(item => item.libraryTrackId != null).Cast<object>().ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static double? Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null; error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession found)
        { error = Unauthorized(new { error = "Authentication required" }); return false; }
        if (!found.TenantId.HasValue || !found.AllstarrUserId.HasValue)
        { error = StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" }); return false; }
        session = found; return true;
    }

    private sealed record MatchRow(TrackMatchState State, string SearchText, object Value);
}
