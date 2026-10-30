using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using allstarr.Services.Common;
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
    ITrackMatchRepository trackMatchCommands,
    IProtocolProviderGateway providerGateway,
    AdminProtocolExecutionContextFactory protocolContexts,
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IHttpClientFactory httpClients,
    IMediaAssetResolver mediaAssets,
    TrackMatchDecisionEngine matcher,
    TrackRematchAllService bulkRematches,
    IEffectiveProviderPolicyResolver? effectivePolicies = null,
    IApplicationCache? applicationCache = null,
    RecordingOdesliEnrichmentQueue? enrichment = null) : ControllerBase
{
    public sealed record ResolveTrackMatchRequest(
        string TargetType,
        Guid? LibraryTrackId = null,
        string? ExternalProvider = null,
        string? ExternalId = null,
        string? Reason = null,
        string AuthorityScope = "personal",
        ManualAuthorityRevision? ExpectedAuthority = null);

    public sealed record ApplyTrackRematchAllRequest(string ConfirmationId);

    public sealed record ManualAuthorityRequest(string Kind, long ExpectedRevision, string AuthorityScope = "personal");

    [HttpGet("{externalSnapshotId:guid}/artwork")]
    public async Task<IActionResult> SourceArtwork(
        Guid externalSnapshotId,
        CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == externalSnapshotId &&
            (session!.IsAdministrator || item.OwnerUserId == session.AllstarrUserId),
            cancellationToken);
        if (snapshot == null) return NotFound();
        var artworkUrl = Metadata(snapshot.PayloadJson).ArtworkUrl;
        if (!snapshot.ProviderId.Equals("spotify", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artworkUri) ||
            artworkUri.Scheme != Uri.UriSchemeHttps ||
            !SpotifyPlaylistCapabilityAdapter.IsAllowedArtworkHost(artworkUri.Host))
            return NotFound();

        var asset = await mediaAssets.ResolveAsync(
            new MediaAssetIdentity(
                snapshot.OwnerUserId,
                snapshot.ProviderAccountId,
                snapshot.ProviderId,
                "track",
                snapshot.ExternalIdHash,
                snapshot.ProviderRevision,
                Width: 96),
            async token =>
            {
                var outcome = await SpotifyPlaylistCapabilityAdapter.DownloadArtworkAsync(
                    httpClients.CreateClient(SpotifyPlaylistCapabilityAdapter.HttpClientName),
                    artworkUri,
                    5 * 1024 * 1024,
                    token);
                if (!outcome.IsSuccess) return null;
                var artwork = outcome.RequireValue();
                return new MediaAssetSource(artwork.Bytes, artwork.ContentType);
            },
            5 * 1024 * 1024,
            cancellationToken);
        if (asset == null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        return File(asset.Bytes, asset.ContentType);
    }

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

        var userId = session!.AllstarrUserId!.Value;
        var detail = await trackMatchCommands.GetDetailAsync(
            new TrackMatchActor(userId, session.IsAdministrator),
            "spotify",
            spotifyId,
            backendItemId,
            cancellationToken);
        var identities = detail.ProviderIdentities;
        var localTracks = detail.LocalTracks;
        var snapshots = detail.Snapshots;
        var decisions = detail.Decisions;
        var overrides = detail.Overrides;
        var artifacts = detail.Artifacts;

        var firstMappedAt = new DateTimeOffset?[]
        {
            identities.Count == 0 ? null : identities.Min(item => item.CreatedAt),
            decisions.Count == 0 ? null : decisions.Min(item => item.DecidedAt),
            overrides.Count == 0 ? null : overrides.Min(item => item.CreatedAt)
        }.Where(item => item.HasValue).Min();
        var lastMappedAt = new DateTimeOffset?[]
        {
            identities.Count == 0 ? null : identities.Max(item => item.UpdatedAt),
            decisions.Count == 0 ? null : decisions.Max(item => item.DecidedAt),
            overrides.Count == 0 ? null : overrides.Max(item => item.CreatedAt)
        }.Where(item => item.HasValue).Max();

        var activity = new List<object>();
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
        if (backendItemId != null && localTracks.Count == 0)
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
        var latestSnapshot = snapshots.FirstOrDefault(item => item.ProviderId == "spotify" &&
            (item.ExternalIdHash == Hash(spotifyId) || identities.Any(identity =>
                identity.Id == item.ProviderTrackIdentityId && identity.ExternalId == spotifyId))) ?? snapshots.FirstOrDefault();
        TrackMatchTarget? target = null;
        if (latestSnapshot != null)
        {
            var sourceIdentity = identities.FirstOrDefault(item => item.Id == latestSnapshot.ProviderTrackIdentityId) ??
                                 identities.FirstOrDefault(item => MatchesSourceIdentity(latestSnapshot, item));
            var manual = ManualTrackOverrides.Index(snapshots, overrides, userId)
                [latestSnapshot.Id].Effective;
            IReadOnlyList<string> playableProviders = [];
            try
            {
                var execution = await protocolContexts.CreateAsync(session, HttpContext.TraceIdentifier, cancellationToken);
                playableProviders = await providerGateway.GetPlayableProviderOrderAsync(
                    execution.RequireActor(), cancellationToken);
            }
            catch (UnauthorizedAccessException) { }
            var sourceSnapshotIds = snapshots.Where(item => item.ProviderId == latestSnapshot.ProviderId &&
                    item.ExternalIdHash == latestSnapshot.ExternalIdHash).Select(item => item.Id).ToHashSet();
            var decision = decisions.FirstOrDefault(item => sourceSnapshotIds.Contains(item.ExternalSnapshotId));
            var projection = TrackRouteProjector.Project(latestSnapshot, decision, manual,
                sourceIdentity, localTracks, identities, playableProviders,
                effectivePolicies == null ? null : await effectivePolicies.ResolveForUserAsync(userId, cancellationToken));
            target = TrackMatchTargetResolver.Create(projection, decision);
            if (target != null)
                await new TrackMatchTargetResolver(contextFactory, applicationCache, enrichment).EnrichAsync(
                    new(userId, session.IsAdministrator),
                    [new(projection.CanonicalRecordingId, latestSnapshot.OwnerUserId, target, latestSnapshot,
                        projection.PrimaryProviderRoute?.IsManual == true)], cancellationToken);
        }
        var sourceMetadata = latestSnapshot == null ? default : Metadata(latestSnapshot.PayloadJson);
        var effectiveDuration = primaryLocal?.DurationMilliseconds ?? sourceMetadata.DurationMilliseconds;
        return Ok(new
        {
            spotifyId,
            target,
            found = identities.Count > 0 || decisions.Count > 0 || localTracks.Count > 0,
            firstMappedAt,
            lastMappedAt,
            durationMilliseconds = effectiveDuration,
            durationProvenance = primaryLocal?.DurationMilliseconds.HasValue == true
                ? primaryLocal.DurationProvenance ?? primaryLocal.Protocol
                : sourceMetadata.DurationMilliseconds.HasValue ? latestSnapshot?.ProviderId : null,
            durationRetrievedAt = primaryLocal?.DurationMilliseconds.HasValue == true
                ? primaryLocal.DurationRetrievedAt ?? primaryLocal.IndexedAt
                : sourceMetadata.DurationMilliseconds.HasValue ? latestSnapshot?.RetrievedAt : null,
            metadata = new
            {
                title = primaryLocal?.Title,
                artist = primaryLocal?.Artist,
                album = primaryLocal?.Album,
                artworkUrl = primaryLocal?.CoverArtReference == null
                    ? sourceMetadata.ArtworkUrl == null || latestSnapshot == null
                        ? ProviderArtworkFallback.CoverPath(latestSnapshot?.ProviderId ?? "spotify")
                        : SourceArtworkUrl(latestSnapshot.Id)
                    : LocalArtworkUrl(primaryLocal.BackendItemId),
                sourceArtworkUrl = sourceMetadata.ArtworkUrl == null || latestSnapshot == null
                    ? ProviderArtworkFallback.CoverPath(latestSnapshot?.ProviderId ?? "spotify")
                    : SourceArtworkUrl(latestSnapshot.Id),
                candidateArtworkUrl = primaryLocal?.CoverArtReference == null
                    ? null
                    : LocalArtworkUrl(primaryLocal.BackendItemId),
                isrc = primaryLocal?.Isrc,
                musicBrainzRecordingId = primaryLocal?.MusicBrainzRecordingId
            },
            providerIdentities = identities
                .DistinctBy(item => (AppleMusicClient.NormalizeProviderId(item.ProviderId), item.ExternalId))
                .Select(item => new
                {
                    item.Id,
                    item.CanonicalRecordingId,
                    ProviderId = AppleMusicClient.NormalizeProviderId(item.ProviderId),
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
                item.BackendLibraryId,
                item.Title,
                item.Artist,
                item.Album,
                item.AlbumArtist,
                item.DurationMilliseconds,
                item.DurationProvenance,
                item.DurationRetrievedAt,
                item.Isrc,
                item.MusicBrainzRecordingId,
                artworkUrl = item.CoverArtReference == null ? null : LocalArtworkUrl(item.BackendItemId),
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
        [FromQuery] string? backendLibraryId = null,
        [FromQuery] string? state = null,
        [FromQuery] string? search = null,
        [FromQuery] Guid? externalSnapshotId = null,
        [FromQuery] string? sort = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (page < 1 || pageSize is < 1 or > 200) return BadRequest(new { error = "Page and pageSize are outside the supported range" });
        if (!string.IsNullOrWhiteSpace(state) &&
            !state.Equals("attention", StringComparison.OrdinalIgnoreCase) &&
            !state.Equals("matched", StringComparison.OrdinalIgnoreCase) &&
            !state.Equals("history", StringComparison.OrdinalIgnoreCase) &&
            !state.Equals("automatic", StringComparison.OrdinalIgnoreCase) &&
            !state.Equals("manual_matches", StringComparison.OrdinalIgnoreCase) &&
            !state.Equals("manual_rejections", StringComparison.OrdinalIgnoreCase) &&
            !Enum.TryParse<TrackMatchState>(state, true, out _))
            return BadRequest(new { error = "State is not a valid match state" });
        if (sort is not (null or "" or "confidence_desc" or "confidence_asc"))
            return BadRequest(new { error = "Sort is not supported" });

        var userId = session!.AllstarrUserId!.Value;
        var review = await trackMatchCommands.GetReviewDataAsync(
            new TrackMatchActor(userId, session.IsAdministrator),
            backendLibraryId,
            search,
            externalSnapshotId,
            cancellationToken: cancellationToken);
        var decisions = review.LatestDecisions.ToDictionary(item => item.ExternalSnapshotId);
        var overrides = ManualTrackOverrides.Index(review.Snapshots, review.ActiveOverrides, userId);
        var library = review.LibraryTracks.ToDictionary(item => item.Id);
        var sourceIdentities = review.ProviderIdentities.ToDictionary(item => item.Id);
        var sourceIdentitiesByHash = review.ProviderIdentities
            .GroupBy(item => item.ExternalIdHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
                .ThenByDescending(item => item.VerifiedAt)
                .ToArray(), StringComparer.Ordinal);
        var identities = review.ProviderIdentities
            .GroupBy(item => item.CanonicalRecordingId).ToDictionary(group => group.Key, group => group.ToArray());
        ProtocolExecutionContext execution;
        try
        {
            execution = await protocolContexts.CreateAsync(session!, HttpContext.TraceIdentifier, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        }
        var playableProviders = await providerGateway.GetPlayableProviderOrderAsync(execution.RequireActor(), cancellationToken);
        var providerPolicy = effectivePolicies == null ? execution.Policy :
            await effectivePolicies.ResolveForUserAsync(userId, cancellationToken);

        var allRows = review.Snapshots
            .GroupBy(snapshot => new
            {
                snapshot.OwnerUserId,
                SourceIdentity = $"{snapshot.ProviderId.ToLowerInvariant()}:{snapshot.ExternalIdHash}"
            })
            .Select(group =>
            {
                var snapshot = group
                    .OrderByDescending(item => item.RetrievedAt)
                    .ThenByDescending(item => item.SnapshotVersion)
                    .First();
                var decision = group
                    .Select(item => decisions.GetValueOrDefault(item.Id))
                    .Where(item => item != null)
                    .OrderByDescending(item => item!.DecidedAt)
                    .FirstOrDefault();
                var layers = overrides[snapshot.Id];
                var sourceIdentity = snapshot.ProviderTrackIdentityId.HasValue
                    ? sourceIdentities.GetValueOrDefault(snapshot.ProviderTrackIdentityId.Value)
                    : null;
                sourceIdentity ??= sourceIdentitiesByHash.GetValueOrDefault(snapshot.ExternalIdHash)?
                    .FirstOrDefault(item => MatchesSourceIdentity(snapshot, item));
                return Row(
                    snapshot,
                    decision,
                    layers,
                    sourceIdentity,
                    library,
                    identities,
                    playableProviders,
                    userId, session.IsAdministrator, providerPolicy, review.MediaSnapshots.GetValueOrDefault(snapshot.Id));
            })
            .ToArray();
        var filteredRows = allRows.Where(row => state?.ToLowerInvariant() switch
        {
            "manual_matches" => row.HasManualMatch,
            "manual_rejections" => row.HasManualRejection,
            "automatic" => IsAutomaticDecision(row.State, row.HasManualMatch, row.HasManualRejection),
            _ => MatchesStateFilter(row.State, state)
        });
        var rows = sort switch
        {
            "confidence_desc" => filteredRows.OrderByDescending(row => row.Confidence).ToArray(),
            "confidence_asc" => filteredRows.OrderBy(row => row.Confidence).ToArray(),
            _ => filteredRows.ToArray()
        };
        var total = rows.Length;
        var pageRows = rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        await new TrackMatchTargetResolver(contextFactory, applicationCache, enrichment).EnrichAsync(
            new(userId, session.IsAdministrator),
            pageRows.Select(row => row.TargetLookup).OfType<TrackMatchTargetLookup>().ToArray(), cancellationToken);
        var items = pageRows.Select(row => row.Value).ToArray();
        return Ok(new
        {
            matches = items,
            stats = new
            {
                total = allRows.Length,
                matched = allRows.Count(item => item.State is TrackMatchState.Accepted or TrackMatchState.Pinned),
                accepted = allRows.Count(item => item.State is TrackMatchState.Accepted or TrackMatchState.Pinned),
                unresolved = allRows.Count(item => item.State == TrackMatchState.Unresolved),
                suggested = allRows.Count(item => item.State == TrackMatchState.Suggested),
                review = allRows.Count(item => item.State is TrackMatchState.Suggested or TrackMatchState.Ambiguous),
                ambiguous = allRows.Count(item => item.State == TrackMatchState.Ambiguous),
                rejected = allRows.Count(item => item.State == TrackMatchState.Rejected),
                manualMatches = allRows.Count(item => item.HasManualMatch),
                manualRejections = allRows.Count(item => item.HasManualRejection),
                automatic = allRows.Count(item => IsAutomaticDecision(
                    item.State,
                    item.HasManualMatch,
                    item.HasManualRejection)),
                attention = allRows.Count(item => item.State is
                    TrackMatchState.Suggested or TrackMatchState.Ambiguous)
            },
            pagination = new { page, pageSize, total, totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) }
        }
        );
    }

    [HttpGet("targets/local")]
    public async Task<IActionResult> SearchLocalTargets(
        [FromQuery] string query,
        [FromQuery] string? backendLibraryId = null,
        [FromQuery] Guid? externalSnapshotId = null,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        query = query?.Trim() ?? string.Empty;
        if (query.Length < 2) return BadRequest(new { error = "Enter at least two characters" });
        limit = Math.Clamp(limit, 1, 50);

        var userId = session!.AllstarrUserId!.Value;
        var source = await ReviewSourceAsync(session!, externalSnapshotId, cancellationToken);
        var tracks = await trackMatchCommands.SearchLocalTracksAsync(
            new TrackMatchActor(userId, session.IsAdministrator),
            query,
            backendLibraryId,
            limit,
            source,
            cancellationToken);
        var effectiveMatcher = await MatchingEngineAsync(userId, cancellationToken);
        var scores = source == null
            ? []
            : effectiveMatcher.ScoreCandidates(source, tracks.Select(ToCandidate))
                .ToDictionary(item => item.LibraryTrackId);
        var values = tracks.Select(item => new
        {
            id = item.Id,
            backendItemId = item.BackendItemId,
            title = item.Title,
            artist = item.Artist,
            album = item.Album,
            durationMilliseconds = item.DurationMilliseconds,
            isrc = item.Isrc,
            isExplicit = (bool?)null,
            routingPriority = 0,
            matchesSourceAlbum = MatchesSourceAlbum(source?.Album, item.Album),
            media = (object?)null,
            confidence = scores.GetValueOrDefault(item.Id)?.Confidence,
            components = scores.GetValueOrDefault(item.Id)?.Components,
            reasons = scores.GetValueOrDefault(item.Id)?.Reasons,
            warnings = scores.GetValueOrDefault(item.Id)?.Warnings,
            artworkUrl = item.CoverArtReference == null ? null : LocalArtworkUrl(item.BackendItemId)
        }).OrderByDescending(item => item.confidence)
            .ThenByDescending(item => item.matchesSourceAlbum == true)
            .ThenBy(item => OrderKey(item.backendItemId), StringComparer.Ordinal)
            .ThenBy(item => OrderKey(item.title), StringComparer.Ordinal)
            .ToArray();
        return Ok(new { tracks = values });
    }

    [HttpGet("targets/provider")]
    public async Task<IActionResult> SearchProviderTargets(
        [FromQuery] string query,
        [FromQuery] Guid? externalSnapshotId = null,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default,
        [FromQuery] bool includeLocal = false)
    {
        if (!TrySession(out var session, out var error)) return error!;
        query = query?.Trim() ?? string.Empty;
        if (query.Length < 2) return BadRequest(new { error = "Enter at least two characters" });
        limit = Math.Clamp(limit, 1, 50);

        var userId = session!.AllstarrUserId!.Value;
        var streamingOrder = await ProviderOrderAsync(
            userId, ProviderCapabilityKind.Streaming, cancellationToken);
        var downloadOrder = await ProviderOrderAsync(
            userId, ProviderCapabilityKind.Download, cancellationToken);
        var playableProviders = streamingOrder
            .Concat(downloadOrder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (playableProviders.Length == 0 && !includeLocal)
            return Ok(new { tracks = Array.Empty<object>(), providers = playableProviders });

        ProtocolExecutionContext execution;
        try
        {
            execution = await protocolContexts.CreateAsync(
                session!, HttpContext.TraceIdentifier, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        }
        var source = await ReviewSourceAsync(session!, externalSnapshotId, cancellationToken);
        var songs = playableProviders.Length == 0 ? [] : await SearchAsync(query);
        var missingProviders = playableProviders.Where(provider =>
            !songs.Any(song => SameProvider(provider, song.ExternalProvider!))).ToArray();
        if (missingProviders.Length > 0 && source != null && source.Title.Trim().Length >= 2 &&
            !string.IsNullOrWhiteSpace(source.Artist))
        {
            var normalizedQuery = TrackMatchReviewSearch.Normalize(query);
            var normalizedTitle = TrackMatchReviewSearch.Normalize(source.Title);
            if (normalizedQuery != normalizedTitle &&
                (normalizedQuery == TrackMatchReviewSearch.Normalize($"{source.Title} {source.Artist}") ||
                 normalizedQuery == TrackMatchReviewSearch.Normalize($"{source.Artist} {source.Title}")))
            {
                var fallback = await SearchAsync(source.Title.Trim());
                // Keep missing-source alternatives alongside the original bounded search.
                songs = songs.Concat(fallback.Where(song => missingProviders.Any(provider =>
                        SameProvider(provider, song.ExternalProvider!))))
                    .ToArray();
            }
        }
        var effectiveMatcher = await MatchingEngineAsync(userId, cancellationToken);
        IReadOnlyList<LibraryTrackRecord> localTracks = [];
        var partialSearch = false;
        if (includeLocal)
        {
            try
            {
                localTracks = await trackMatchCommands.SearchLocalTracksAsync(new TrackMatchActor(userId, session.IsAdministrator),
                    query, null, limit, source, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                partialSearch = true;
            }
        }
        var candidates = songs
            .DistinctBy(song => TrackMatchPlayableSearch.CandidateId(song.ExternalProvider!, song.ExternalId ?? song.Id))
            .Select(song => new
            {
                Song = (Song?)song,
                LocalTrack = (LibraryTrackRecord?)null,
                Candidate = ToCandidate(song, userId)
            }).Concat(localTracks.Select(track => new
            {
                Song = (Song?)null,
                LocalTrack = (LibraryTrackRecord?)track,
                Candidate = ToCandidate(track)
            })).ToArray();
        var automaticProviders = effectivePolicies == null
            ? streamingOrder
            : (await effectivePolicies.ResolveForUserAsync(userId, cancellationToken))
                .ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Streaming, streamingOrder);
        var automaticCandidates = candidates.Where(item => item.Candidate.IsLocal ||
            automaticProviders.Any(provider => SameProvider(provider, item.Song!.ExternalProvider!))).ToArray();
        var orderedScores = source == null
            ? []
            : effectiveMatcher.ScoreCandidates(source, automaticCandidates.Select(item => item.Candidate), automaticProviders)
                .Concat(effectiveMatcher.ScoreCandidates(source,
                    candidates.Except(automaticCandidates).Select(item => item.Candidate), streamingOrder))
                .ToArray();
        var scores = orderedScores.ToDictionary(item => item.LibraryTrackId);
        var automaticOrder = orderedScores.Select((score, index) => (score.LibraryTrackId, Index: index))
            .ToDictionary(item => item.LibraryTrackId, item => item.Index);
        var ranked = candidates
            .Select(item => new
            {
                item.Song,
                item.LocalTrack,
                item.Candidate,
                Score = scores.GetValueOrDefault(item.Candidate.LibraryTrackId),
                AutomaticOrder = automaticOrder.GetValueOrDefault(item.Candidate.LibraryTrackId, int.MaxValue),
                RoutingPriority = item.Candidate.IsLocal ? 0 : ProviderRoutingPriority(item.Song!.ExternalProvider!, streamingOrder),
                MatchesSourceAlbum = MatchesSourceAlbum(source?.Album, item.Candidate.Album)
            })
            .OrderBy(item => item.AutomaticOrder)
            .ThenByDescending(item => item.Score?.Confidence)
            .ThenBy(item => item.RoutingPriority ?? int.MaxValue)
            .ThenByDescending(item => item.MatchesSourceAlbum == true)
            .ThenBy(item => OrderKey(item.Song?.ExternalProvider), StringComparer.Ordinal)
            .ThenBy(item => OrderKey(item.Song?.ExternalId ?? item.Song?.Id ?? item.Candidate.BackendItemId), StringComparer.Ordinal)
            .ThenBy(item => OrderKey(item.Candidate.Title), StringComparer.Ordinal)
            .ToArray();
        return Ok(new
        {
            tracks = ranked.Select(item =>
            {
                var song = item.Song;
                return new
                {
                    id = item.LocalTrack?.Id.ToString() ?? song?.ExternalId,
                    backendItemId = item.LocalTrack?.BackendItemId,
                    externalId = song?.ExternalId,
                    externalProvider = song?.ExternalProvider,
                    isBestVersion = song?.IsBestVersion == true && string.Equals(song.ExternalProvider, "artistgrid", StringComparison.OrdinalIgnoreCase),
                    title = item.Candidate.Title,
                    artist = item.Candidate.Artist,
                    album = item.Candidate.Album,
                    artworkUrl = item.LocalTrack != null
                        ? item.LocalTrack.CoverArtReference == null ? null : LocalArtworkUrl(item.LocalTrack.BackendItemId)
                        : string.IsNullOrWhiteSpace(song?.CoverArtUrl) || string.IsNullOrWhiteSpace(song.ExternalId)
                            ? null : ExternalArtworkUrl(song.ExternalProvider!, song.ExternalId!),
                    durationMilliseconds = item.Candidate.DurationMilliseconds,
                    isrc = item.Candidate.Isrc,
                    isExplicit = item.Candidate.IsExplicit,
                    routingPriority = item.RoutingPriority,
                    automaticOrder = source == null ? (int?)null : item.AutomaticOrder,
                    matchesSourceAlbum = item.MatchesSourceAlbum,
                    media = song == null ? null : ReportedMedia(song),
                    confidence = item.Score?.Confidence,
                    components = item.Score?.Components,
                    reasons = item.Score?.Reasons,
                    warnings = item.Score?.Warnings
                };
            }),
            providers = playableProviders,
            partialSearch
        });

        async Task<Song[]> SearchAsync(string searchQuery) =>
            (await providerGateway.SearchPlayableSongsAsync(execution, searchQuery, limit))
                .Where(song => !string.IsNullOrWhiteSpace(song.ExternalProvider) &&
                    playableProviders.Any(item => SameProvider(item, song.ExternalProvider)))
                .Take(limit)
                .ToArray();
    }

    [HttpPost("{externalSnapshotId:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid externalSnapshotId,
        [FromBody] ResolveTrackMatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        var result = await trackMatchCommands.ResolveSnapshotAsync(
            new TrackMatchActor(
                session!.AllstarrUserId!.Value,
                session.IsAdministrator),
            externalSnapshotId,
            new ResolveTrackMatchCommand(
                request.TargetType,
                request.LibraryTrackId,
                ExternalProvider: request.ExternalProvider,
                ExternalId: request.ExternalId,
                Reason: request.Reason,
                AuthorityScope: request.AuthorityScope,
                ExpectedAuthority: request.ExpectedAuthority),
            HttpContext.TraceIdentifier,
            cancellationToken);

        if (result.Succeeded) return Ok(new { success = true });
        return result.Failure switch
        {
            TrackMatchCommandFailure.Invalid => BadRequest(new { error = result.Error }),
            TrackMatchCommandFailure.NotFound => NotFound(new { error = result.Error }),
            TrackMatchCommandFailure.Forbidden => StatusCode(403, new { error = result.Error }),
            TrackMatchCommandFailure.Conflict => Conflict(new { error = result.Error }),
            _ => StatusCode(500, new { error = result.Error ?? "Failed to resolve track match" })
        };
    }

    [HttpPost("{externalSnapshotId:guid}/rematch")]
    public async Task<IActionResult> Rematch(Guid externalSnapshotId, CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        ProtocolExecutionContext execution;
        try
        {
            execution = await protocolContexts.CreateAsync(
                session!, HttpContext.TraceIdentifier, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        }
        var result = await trackMatchCommands.RematchSnapshotAsync(
            execution,
            externalSnapshotId,
            HttpContext.TraceIdentifier,
            "manual-rematch-v3",
            cancellationToken);

        if (!result.Succeeded)
        {
            return result.Failure switch
            {
                TrackMatchCommandFailure.NotFound => NotFound(new { error = result.Error }),
                TrackMatchCommandFailure.Forbidden => StatusCode(403, new { error = result.Error }),
                TrackMatchCommandFailure.Invalid => BadRequest(new { error = result.Error }),
                TrackMatchCommandFailure.Conflict => Conflict(new { error = result.Error }),
                TrackMatchCommandFailure.LocalLibraryUnavailable => StatusCode(503,
                    new { error = result.Error, code = "local_library_unavailable", retryable = true }),
                _ => StatusCode(500, new { error = result.Error ?? "Failed to rematch track" })
            };
        }

        return Ok(new
        {
            rematched = true,
            state = result.State,
            confidence = result.Confidence,
            candidateCount = result.CandidateCount,
            decisionVersion = result.DecisionVersion
        });
    }

    [HttpDelete("{externalSnapshotId:guid}/manual-authorities/{authorityId:guid}")]
    public async Task<IActionResult> DeleteManualAuthority(
        Guid externalSnapshotId,
        Guid authorityId,
        [FromQuery] string kind,
        [FromQuery] long? expectedRevision,
        CancellationToken cancellationToken = default,
        [FromQuery] string authorityScope = "personal")
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (!TryManualAuthorityKind(kind, out var authorityKind))
            return BadRequest(new { error = "Manual authority kind is invalid" });
        if (!expectedRevision.HasValue)
            return BadRequest(new { error = "ExpectedRevision is required" });
        var result = await trackMatchCommands.ClearManualAuthorityAsync(
            new TrackMatchActor(
                session!.AllstarrUserId!.Value,
                session.IsAdministrator),
            externalSnapshotId,
            authorityKind,
            authorityId,
            expectedRevision.Value,
            HttpContext.TraceIdentifier,
            cancellationToken, authorityScope);
        return result.Succeeded
            ? NoContent()
            : MatchCommandError(result.Failure, result.Error, "Failed to delete the manual decision");
    }

    [HttpPost("{externalSnapshotId:guid}/manual-authorities/{authorityId:guid}/rematch")]
    public async Task<IActionResult> RematchManualAuthority(
        Guid externalSnapshotId,
        Guid authorityId,
        [FromBody] ManualAuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (!TryManualAuthorityKind(request.Kind, out var authorityKind))
            return BadRequest(new { error = "Manual authority kind is invalid" });
        ProtocolExecutionContext execution;
        try
        {
            execution = await protocolContexts.CreateAsync(
                session!, HttpContext.TraceIdentifier, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        }
        var result = await trackMatchCommands.RematchManualAuthorityAsync(
            execution,
            externalSnapshotId,
            authorityKind,
            authorityId,
            request.ExpectedRevision,
            HttpContext.TraceIdentifier,
            cancellationToken, request.AuthorityScope);
        if (!result.Succeeded)
            return MatchCommandError(result.Failure, result.Error, "Failed to rematch the manual decision");
        return Ok(new
        {
            rematched = true,
            state = result.State,
            confidence = result.Confidence,
            candidateCount = result.CandidateCount,
            decisionVersion = result.DecisionVersion
        });
    }

    [HttpGet("rematch-all/preview")]
    public async Task<IActionResult> PreviewRematchAll(CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        var scopeOwnerUserId = session!.IsAdministrator
            ? null
            : session.AllstarrUserId;
        var preview = await bulkRematches.PreviewAsync(
            new TrackMatchActor(session.AllstarrUserId!.Value, session.IsAdministrator),
            scopeOwnerUserId,
            cancellationToken);
        return Ok(new
        {
            confirmationId = preview.ConfirmationId,
            algorithmVersion = preview.AlgorithmVersion,
            totalTracks = preview.TotalTracks,
            resolvedTracks = preview.ResolvedTracks,
            reviewTracks = preview.ReviewTracks,
            unresolvedTracks = preview.UnresolvedTracks,
            protectedManualTracks = preview.ProtectedManualTracks,
            automaticDecisionsToReplace = preview.AutomaticDecisionsToReplace,
            tracksToRematch = preview.TracksToRematch,
            canApply = preview.CanApply
        });
    }

    [HttpPost("rematch-all/apply")]
    public async Task<IActionResult> ApplyRematchAll(
        [FromBody] ApplyTrackRematchAllRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (request?.ConfirmationId is not { Length: 64 } ||
            request.ConfirmationId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return BadRequest(new { error = "Review the rematch preview again before applying it." });
        var scopeOwnerUserId = session!.IsAdministrator
            ? null
            : session.AllstarrUserId;
        var preview = await bulkRematches.PreviewAsync(
            new TrackMatchActor(session.AllstarrUserId!.Value, session.IsAdministrator),
            scopeOwnerUserId,
            cancellationToken);
        if (!preview.ConfirmationId.Equals(request.ConfirmationId, StringComparison.Ordinal))
            return Conflict(new { error = "The match state changed. Review the rematch preview again." });
        if (!preview.CanApply)
            return Conflict(new { error = "No automatic track decisions are available to rematch." });
        var queued = await bulkRematches.QueueForceAsync(
            new TrackMatchActor(session.AllstarrUserId!.Value, session.IsAdministrator),
            preview,
            cancellationToken);
        return Accepted(new
        {
            jobId = queued.JobId,
            created = queued.Created,
            algorithmVersion = preview.AlgorithmVersion,
            trackCount = preview.TracksToRematch
        });
    }

    private static bool MatchesStateFilter(TrackMatchState state, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        (filter.Equals("attention", StringComparison.OrdinalIgnoreCase) && state is
            TrackMatchState.Suggested or TrackMatchState.Ambiguous) ||
        (filter.Equals("matched", StringComparison.OrdinalIgnoreCase) && state is TrackMatchState.Accepted or TrackMatchState.Pinned) ||
        (filter.Equals("history", StringComparison.OrdinalIgnoreCase) && state is
            TrackMatchState.Accepted or TrackMatchState.Pinned or TrackMatchState.Rejected) ||
        state.ToString().Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static bool IsAutomaticDecision(
        TrackMatchState state,
        bool hasManualMatch,
        bool hasManualRejection) =>
        state == TrackMatchState.Accepted && !hasManualMatch && !hasManualRejection;

    private static bool TryManualAuthorityKind(string? value, out ManualTrackAuthorityKind kind)
    {
        kind = value?.Trim().ToLowerInvariant() switch
        {
            "local_match" => ManualTrackAuthorityKind.LocalMatch,
            "provider_match" => ManualTrackAuthorityKind.ProviderMatch,
            "rejection" => ManualTrackAuthorityKind.Rejection,
            _ => default
        };
        return value?.Trim().ToLowerInvariant() is "local_match" or "provider_match" or "rejection";
    }

    private IActionResult MatchCommandError(
        TrackMatchCommandFailure failure,
        string? message,
        string fallback) => failure switch
        {
            TrackMatchCommandFailure.Invalid => BadRequest(new { error = message ?? fallback }),
            TrackMatchCommandFailure.NotFound => NotFound(new { error = message ?? fallback }),
            TrackMatchCommandFailure.Forbidden => StatusCode(403, new { error = message ?? fallback }),
            TrackMatchCommandFailure.Conflict => Conflict(new { error = message ?? fallback }),
            _ => StatusCode(500, new { error = message ?? fallback })
        };

    private static bool MatchesSourceIdentity(
        ExternalMetadataSnapshotRecord snapshot,
        ProviderTrackIdentityRecord identity) =>
        identity.ProviderId.Equals(snapshot.ProviderId, StringComparison.OrdinalIgnoreCase) &&
        (identity.ProviderAccountId == snapshot.ProviderAccountId ||
         identity.Scope == ProviderIdentityScope.Catalog && !identity.ProviderAccountId.HasValue) &&
        identity.ResourceKind == ProviderResourceKind.Track &&
        identity.Scope is ProviderIdentityScope.Catalog or ProviderIdentityScope.Account &&
        identity.ExternalIdHash == snapshot.ExternalIdHash &&
        identity.Verification is ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned;

    private static string CleanReason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static MatchRow Row(ExternalMetadataSnapshotRecord snapshot, TrackMatchRecord? decision,
        ManualTrackOverrideLayers layers, ProviderTrackIdentityRecord? sourceIdentity,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord[]> identities,
        IReadOnlyCollection<string> playableProviders, Guid viewerUserId, bool isAdministrator,
        EffectiveProviderPolicySnapshot? providerPolicy = null,
        ExternalMetadataSnapshotRecord? mediaSnapshot = null)
    {
        var manual = layers.Effective;
        var routeCanonicalId = decision?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
        var routeIdentities = routeCanonicalId.HasValue &&
                              identities.TryGetValue(routeCanonicalId.Value, out var canonicalIdentities)
            ? canonicalIdentities
            : [];
        var projection = TrackRouteProjector.Project(
            snapshot,
            decision,
            manual,
            sourceIdentity,
            library.Values,
            routeIdentities,
            playableProviders, providerPolicy);
        var playableProviderSet = playableProviders.ToHashSet(StringComparer.Ordinal);
        var indexedLibraryTrackIds = library.Keys.ToHashSet();
        var state = projection.ReviewState;
        var track = projection.LibraryTrack;
        var trackId = track?.Id;
        var canonicalId = projection.CanonicalRecordingId;
        var target = TrackMatchTargetResolver.Create(projection, decision);
        var providerIdentities = projection.ProviderIdentities
            .DistinctBy(item => (AppleMusicClient.NormalizeProviderId(item.ProviderId), item.ExternalId))
            .Select(item => new
            {
                id = item.Id,
                providerId = AppleMusicClient.NormalizeProviderId(item.ProviderId),
                externalId = item.ExternalId,
                scope = item.Scope.ToString(),
                verification = item.Verification.ToString(),
                verificationMethod = item.VerificationMethod,
                revision = item.Revision
            }).ToArray();
        var manualAuthorities = layers.All.Select(authority => new ManualAuthorityRow(
            authority.Id,
            authority.Decision == ManualOverrideDecision.Reject ? "rejection" :
                authority.TargetProviderId != null ? "provider_match" : "local_match",
            authority.Revision,
            snapshot.Id,
            authority.Reason,
            authority.CreatedAt,
            authority.Id == manual?.Id && (authority.Decision != ManualOverrideDecision.Reject ||
                TrackMatchOverridePolicy.IsEffectiveRejection(authority, decision)),
            authority.OwnerUserId.HasValue ? "personal" : "household",
            authority.OwnerUserId == viewerUserId || authority.OwnerUserId == null && isAdministrator,
            authority.TargetProviderId,
            authority.TargetExternalId)).ToList();
        var hasManualMatch = manualAuthorities.Any(item => item.Kind != "rejection");
        var hasManualRejection = manualAuthorities.Any(item => item.Kind == "rejection");
        var metadata = Metadata(snapshot.PayloadJson);
        var measured = new Song();
        if (mediaSnapshot != null)
        {
            try
            {
                if (JsonSerializer.Deserialize<allstarr.Core.Music.TrackMediaFacts>(mediaSnapshot.PayloadJson) is { Media: not null } facts)
                    facts.Apply(measured);
            }
            catch (JsonException) { }
        }
        var measuredDuration = measured.PreciseDuration is { } duration ? (long?)Math.Round(duration.TotalMilliseconds) : null;
        var sourceDuration = measuredDuration ?? metadata.DurationMilliseconds;
        var sourceArtworkUrl = metadata.ArtworkUrl == null
            ? ProviderArtworkFallback.CoverPath(snapshot.ProviderId)
            : SourceArtworkUrl(snapshot.Id);
        var candidateArtworkUrl = track?.CoverArtReference == null
            ? null
            : LocalArtworkUrl(track.BackendItemId);
        var value = new
        {
            externalSnapshotId = snapshot.Id,
            providerId = snapshot.ProviderId,
            providerAccountId = snapshot.ProviderAccountId,
            state = state.ToString().ToLowerInvariant(),
            decisionSource = manualAuthorities.Count > 0
                ? "manual_authority"
                : decision != null
                    ? "track_match_decision"
                    : sourceIdentity != null
                        ? "canonical_provider_identity"
                        : "unresolved",
            confidence = decision?.Confidence,
            threshold = decision?.Threshold,
            decisionVersion = decision?.DecisionVersion,
            algorithmVersion = decision?.MatcherVersion,
            policyVersion = decision?.PolicyVersion,
            sourceSnapshotVersion = decision?.SourceSnapshotVersion,
            libraryIndexRevision = decision?.LibraryIndexRevision,
            canonicalRecordingId = canonicalId,
            libraryTrackId = trackId,
            overrideId = manual?.Id,
            overrideRevision = manual?.Revision,
            manualAuthorities,
            allowedAuthorityScopes = isAdministrator ? new[] { "personal", "household" } : ["personal"],
            effectiveAuthorityScope = layers.EffectiveScope,
            title = metadata.Title,
            searchQuery = metadata.Title ?? string.Empty,
            artist = metadata.Artist,
            album = metadata.Album,
            artworkUrl = metadata.ArtworkUrl == null ? candidateArtworkUrl ?? sourceArtworkUrl : sourceArtworkUrl,
            sourceArtworkUrl,
            candidateArtworkUrl,
            isrc = metadata.Isrc,
            isExplicit = metadata.IsExplicit,
            durationMilliseconds = sourceDuration,
            media = ReportedMedia(measured),
            durationProvenance = sourceDuration.HasValue
                ? metadata.DurationProvenance ?? snapshot.ProviderId
                : null,
            durationRetrievedAt = sourceDuration.HasValue
                ? (DateTimeOffset?)(measuredDuration.HasValue ? mediaSnapshot!.RetrievedAt : snapshot.RetrievedAt)
                : null,
            localTrack = track == null ? null : new
            {
                id = track.Id,
                backendItemId = track.BackendItemId,
                backendLibraryId = track.BackendLibraryId,
                title = track.Title,
                artist = track.Artist,
                album = track.Album,
                durationMilliseconds = track.DurationMilliseconds,
                durationProvenance = track.DurationProvenance,
                durationRetrievedAt = track.DurationRetrievedAt,
                artworkUrl = candidateArtworkUrl,
                providerIds = ParseObject(track.ProviderIdsJson)
            },
            target,
            providerIdentities,
            candidates = ReviewCandidates(
                snapshot, decision, projection, library, playableProviderSet, indexedLibraryTrackIds),
            reasons = decision == null && sourceIdentity != null
                ? ["Existing canonical provider identity is available."]
                : ParseArray(decision?.ReasonsJson),
            warnings = ParseArray(decision?.WarningsJson),
            decidedAt = decision?.DecidedAt,
            reviewedAt = manualAuthorities.Count == 0
                ? null
                : (DateTimeOffset?)manualAuthorities.Max(item => item.CreatedAt)
        };
        return new(
            state,
            decision?.Confidence,
            $"{metadata.Title} {metadata.Artist} {metadata.Album} {snapshot.ProviderId} {track?.Title} {track?.Artist}",
            hasManualMatch,
            hasManualRejection,
            value,
            target == null ? null : new(canonicalId, snapshot.OwnerUserId, target, snapshot,
                projection.PrimaryProviderRoute?.IsManual == true));
    }

    private static (string? Title, string? Artist, string? Album, string? ArtworkUrl, string? Isrc, long? DurationMilliseconds, bool? IsExplicit, string? DurationProvenance) Metadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = Text(root, "title") ?? Text(root, "Title") ?? Text(root, "name") ?? Text(root, "Name");
            return (
                title,
                TrackSnapshotMetadata.Artist(root),
                Text(root, "album") ?? Text(root, "Album") ?? Text(root, "albumTitle") ?? Text(root, "AlbumTitle"),
                Text(root, "artworkUrl") ?? Text(root, "ArtworkUrl") ?? Text(root, "coverUrl") ?? Text(root, "CoverUrl") ??
                    Text(root, "imageUrl") ?? Text(root, "ImageUrl") ??
                    Text(root, "artworkReference") ?? Text(root, "ArtworkReference"),
                Text(root, "isrc") ?? Text(root, "Isrc") ?? Text(root, "ISRC"),
                DurationMilliseconds(root),
                TrackSnapshotMetadata.IsExplicit(root),
                TrackSnapshotMetadata.DurationProvenance(root));
        }
        catch (JsonException) { return default; }
    }

    private async Task<ExternalTrackMatchSnapshot?> ReviewSourceAsync(
        AdminAuthSession session,
        Guid? externalSnapshotId,
        CancellationToken cancellationToken)
    {
        if (!externalSnapshotId.HasValue) return null;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == externalSnapshotId.Value &&
            (session.IsAdministrator || item.OwnerUserId == session.AllstarrUserId),
            cancellationToken);
        if (snapshot == null) return null;
        var identity = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == snapshot.ProviderTrackIdentityId.Value,
                cancellationToken)
            : null;
        var canonicalId = identity?.CanonicalRecordingId ?? await db.TrackMatches.AsNoTracking()
            .Where(item => item.OwnerUserId == snapshot.OwnerUserId &&
                           item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion)
            .Select(item => item.CanonicalRecordingId)
            .FirstOrDefaultAsync(cancellationToken);
        var metadata = Metadata(snapshot.PayloadJson);
        return new(
            snapshot.Id.ToString("N"),
            identity?.ProviderId ?? snapshot.ProviderId,
            identity?.ExternalId ?? snapshot.ExternalIdHash,
            metadata.Title ?? "Unknown",
            metadata.Artist ?? "Unknown",
            metadata.Album,
            null,
            metadata.DurationMilliseconds,
            metadata.Isrc,
            null,
            metadata.IsExplicit,
            canonicalId)
        {
            DurationProvenance = metadata.DurationProvenance
        };
    }

    private static LocalTrackMatchCandidate ToCandidate(LibraryTrackRecord item) => new(
        item.Id,
        item.OwnerUserId,
        item.BackendInstanceId,
        item.BackendLibraryId,
        item.BackendItemId,
        item.CanonicalRecordingId,
        item.Title,
        item.Artist,
        item.Album,
        item.AlbumArtist,
        item.DurationMilliseconds,
        item.Isrc,
        item.MusicBrainzRecordingId,
        null,
        null);

    private static LocalTrackMatchCandidate ToCandidate(
        Song song,
        Guid ownerUserId) => new(
        TrackMatchPlayableSearch.CandidateId(song.ExternalProvider!, song.ExternalId ?? song.Id),
        ownerUserId,
        string.Empty,
        null,
        song.ExternalId ?? song.Id,
        null,
        song.Title,
        song.Artist,
        song.Album,
        song.AlbumArtist,
        song.Duration * 1000L,
        song.Isrc,
        null,
        song.ExplicitContentLyrics switch
        {
            1 => true,
            0 or 3 => false,
            _ => null
        },
        string.IsNullOrWhiteSpace(song.ExternalProvider) ||
        string.IsNullOrWhiteSpace(song.ExternalId)
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [song.ExternalProvider] = song.ExternalId
            },
        IsLocal: false,
        IsBestVersion: song.IsBestVersion,
        ExternalProvider: song.ExternalProvider);

    private static int? ProviderRoutingPriority(string provider, IReadOnlyList<string> streamingOrder)
    {
        var ordered = streamingOrder.Select(ExternalTrackPlaybackPolicy.Normalize)
            .Distinct(StringComparer.Ordinal).ToArray();
        var index = Array.IndexOf(ordered, ExternalTrackPlaybackPolicy.Normalize(provider));
        return index < 0 ? null : index + 1;
    }

    private static bool? MatchesSourceAlbum(string? sourceAlbum, string? candidateAlbum) =>
        TrackMatchDecisionEngine.SameAlbumEdition(sourceAlbum, candidateAlbum);

    private static string OrderKey(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static object? ReportedMedia(Song song)
    {
        var codec = MediaToken(song.Codec);
        var container = MediaToken(song.Container);
        var bitrate = song.Bitrate > 0 ? song.Bitrate : null;
        var sampleRate = song.SampleRate > 0 ? song.SampleRate : null;
        var bitDepth = codec is "mp3" or "aac" or "opus" or "vorbis"
            ? null
            : song.BitDepth > 0 ? song.BitDepth : null;
        var channels = song.Channels > 0 ? song.Channels : null;
        return codec == null && container == null && bitrate == null && sampleRate == null &&
               bitDepth == null && channels == null
            ? null
            : new { codec, container, bitrate, sampleRate, bitDepth, channels };
    }

    private static string? MediaToken(string? value)
    {
        var token = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(token) || token.Length > 32 || token == "unknown" ||
               !char.IsAsciiLetterOrDigit(token[0]) ||
               token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            ? null
            : token;
    }

    private static long? DurationMilliseconds(JsonElement root)
    {
        if ((root.TryGetProperty("durationMilliseconds", out var value) ||
             root.TryGetProperty("DurationMilliseconds", out value) ||
             root.TryGetProperty("durationMs", out value) ||
             root.TryGetProperty("DurationMs", out value)) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var milliseconds))
            return checked((long)Math.Round(milliseconds));
        return (root.TryGetProperty("durationSeconds", out value) ||
                root.TryGetProperty("DurationSeconds", out value)) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var seconds)
            ? checked((long)Math.Round(seconds * 1000d))
            : null;
    }

    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string LocalArtworkUrl(string backendItemId) =>
        $"/api/admin/downloads/artwork/{Uri.EscapeDataString(backendItemId)}";

    private static string SourceArtworkUrl(Guid externalSnapshotId) =>
        $"/api/admin/track-matches/{externalSnapshotId}/artwork";

    private static string ExternalArtworkUrl(string provider, string externalId) =>
        LocalArtworkUrl($"ext-{provider}-song-{externalId}");
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

    private static object[] ReviewCandidates(
        ExternalMetadataSnapshotRecord snapshot,
        TrackMatchRecord? decision,
        TrackRouteProjection projection,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        IReadOnlySet<string> playableProviders,
        IReadOnlySet<Guid> indexedLibraryTrackIds)
    {
        JsonArray candidates;
        try { candidates = JsonNode.Parse(decision?.CandidateResultsJson ?? "[]") as JsonArray ?? []; }
        catch (JsonException) { candidates = []; }
        var current = projection.LibraryTrack;
        if (current == null && projection.ReviewState == TrackMatchState.Ambiguous && decision?.LibraryTrackId is { } selectedId)
            current = library.GetValueOrDefault(selectedId);
        var route = current == null ? projection.PrimaryProviderRoute : null;
        if (current != null || route != null)
        {
            var metadata = Metadata(snapshot.PayloadJson);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var source = new ExternalTrackMatchSnapshot(snapshot.Id.ToString("N"), snapshot.ProviderId, snapshot.Id.ToString("N"),
                metadata.Title ?? "Unknown", metadata.Artist ?? "Unknown", metadata.Album, null, metadata.DurationMilliseconds,
                metadata.Isrc, null, metadata.IsExplicit)
            {
                DurationProvenance = metadata.DurationProvenance
            };
            var fallback = current != null
                ? new TrackMatchDecisionEngine().ScoreCandidates(source, [ToCandidate(current)]).Single()
                : new TrackMatchCandidateScore(Guid.Empty, $"ext-{route!.ProviderId}-song-{route.ExternalId}", decision?.Confidence ?? 0,
                    [], [], Title: metadata.Title, Artist: metadata.Artist, Album: metadata.Album,
                    DurationMilliseconds: metadata.DurationMilliseconds, SourceIsrc: metadata.Isrc,
                    ProviderTrackIds: new Dictionary<string, string> { [route.ProviderId] = route.ExternalId }, IsLocal: false);
            var existing = candidates.OfType<JsonObject>().FirstOrDefault(node =>
            {
                var item = JsonSerializer.SerializeToElement(node);
                if (current != null)
                    return (Text(item, "libraryTrackId") ?? Text(item, "LibraryTrackId")) == current.Id.ToString() ||
                           (Text(item, "backendItemId") ?? Text(item, "BackendItemId")) == current.BackendItemId;
                var ids = Element(item, "providerTrackIds", "ProviderTrackIds");
                return ids is { ValueKind: JsonValueKind.Object } && ids.Value.EnumerateObject().Any(pair =>
                    SameProvider(pair.Name, route!.ProviderId) && pair.Value.ValueKind == JsonValueKind.String && pair.Value.GetString() == route.ExternalId);
            });
            if (existing == null)
            {
                fallback = fallback with
                {
                    Confidence = decision?.Confidence ?? fallback.Confidence,
                    Reasons = decision == null ? fallback.Reasons : ParseArray(decision.ReasonsJson),
                    Warnings = decision == null ? fallback.Warnings : ParseArray(decision.WarningsJson)
                };
                existing = JsonSerializer.SerializeToNode(fallback, options)!.AsObject();
                candidates.Insert(0, existing);
            }
            existing["isCurrent"] = true;
            existing["isLocal"] = current != null;
            if (current != null) existing["libraryTrackId"] = current.Id.ToString();
            if (route != null)
            {
                var providerIds = new JsonObject { [route.ProviderId] = route.ExternalId };
                if ((existing["providerTrackIds"] ?? existing["ProviderTrackIds"]) is JsonObject savedIds)
                    foreach (var pair in savedIds.Where(pair => !SameProvider(pair.Key, route.ProviderId)))
                        providerIds[pair.Key] = pair.Value?.DeepClone();
                existing["providerTrackIds"] = providerIds;
            }
        }
        return ParseCandidates(candidates.ToJsonString(), playableProviders, indexedLibraryTrackIds);
    }

    private static object[] ParseCandidates(
        string? json,
        IReadOnlySet<string> playableProviders,
        IReadOnlySet<Guid> indexedLibraryTrackIds)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Where(item => Boolean(item, "isCurrent") == true || CredibleCandidate(item))
                .Select(item =>
            {
                var libraryTrackId = Text(item, "libraryTrackId") ?? Text(item, "LibraryTrackId");
                var providerTrackIds = Element(item, "providerTrackIds", "ProviderTrackIds");
                var hasPlayableProvider = HasProviderTrackId(providerTrackIds, playableProviders);
                var isLocal = Boolean(item, "isLocal") ?? Boolean(item, "IsLocal") ??
                    (Guid.TryParse(libraryTrackId, out var id) && indexedLibraryTrackIds.Contains(id)
                        ? true
                        : hasPlayableProvider ? false : null);
                return new
                {
                    libraryTrackId,
                    isCurrent = Boolean(item, "isCurrent") == true,
                    backendItemId = Text(item, "backendItemId") ?? Text(item, "BackendItemId"),
                    confidence = Number(item, "confidence") ?? Number(item, "Confidence"),
                    title = Text(item, "title") ?? Text(item, "Title"),
                    isBestVersion = Boolean(item, "isBestVersion") ?? Boolean(item, "IsBestVersion") ?? false,
                    artist = Text(item, "artist") ?? Text(item, "Artist"),
                    album = Text(item, "album") ?? Text(item, "Album"),
                    durationMilliseconds = Number(item, "durationMilliseconds") ??
                                           Number(item, "DurationMilliseconds") ??
                                           (Number(item, "durationSeconds") ?? Number(item, "DurationSeconds")) * 1000,
                    sourceIsrc = Text(item, "sourceIsrc") ?? Text(item, "SourceIsrc"),
                    candidateIsrc = Text(item, "candidateIsrc") ?? Text(item, "CandidateIsrc"),
                    normalizedSourceTitle = Text(item, "normalizedSourceTitle") ?? Text(item, "NormalizedSourceTitle"),
                    normalizedCandidateTitle = Text(item, "normalizedCandidateTitle") ?? Text(item, "NormalizedCandidateTitle"),
                    artistOverlap = Number(item, "artistOverlap") ?? Number(item, "ArtistOverlap"),
                    albumEvidence = Number(item, "albumEvidence") ?? Number(item, "AlbumEvidence"),
                    durationDeltaMilliseconds = Number(item, "durationDeltaMilliseconds") ??
                                                Number(item, "DurationDeltaMilliseconds") ??
                                                (Number(item, "durationDeltaSeconds") ?? Number(item, "DurationDeltaSeconds")) * 1000,
                    isLocal,
                    providerTrackIds,
                    components = Element(item, "components", "Components"),
                    reasons = Element(item, "reasons", "Reasons"),
                    warnings = Element(item, "warnings", "Warnings")
                };
            }).Where(item => item.isLocal == true && item.libraryTrackId != null ||
                             item.isLocal == false && HasProviderTrackId(item.providerTrackIds, playableProviders))
                .Cast<object>()
                .ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static bool CredibleCandidate(JsonElement item)
    {
        var components = Element(item, "components", "Components");
        if (components is not { ValueKind: JsonValueKind.Object }) return true;
        var title = Number(components.Value, "title") ?? Number(components.Value, "Title");
        var artist = Number(components.Value, "artist") ?? Number(components.Value, "Artist");
        return (title == null || title >= 0.75) && (artist == null || artist >= 0.7);
    }

    private static JsonElement? Element(JsonElement root, string camelName, string pascalName) =>
        root.TryGetProperty(camelName, out var value) || root.TryGetProperty(pascalName, out value)
            ? value.Clone()
            : null;

    private static bool HasProviderTrackId(
        JsonElement? value,
        IReadOnlySet<string> playableProviders) =>
        value is { ValueKind: JsonValueKind.Object } &&
        value.Value.EnumerateObject().Any(item =>
            playableProviders.Contains(ExternalTrackPlaybackPolicy.Normalize(item.Name)) &&
            item.Value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(item.Value.GetString()));

    private static double? Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) ? number : null;

    private static bool? Boolean(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool SameProvider(string left, string right) =>
        ExternalTrackPlaybackPolicy.Normalize(left) == ExternalTrackPlaybackPolicy.Normalize(right);

    private async Task<IReadOnlyList<string>> ProviderOrderAsync(
        Guid userId,
        ProviderCapabilityKind capability,
        CancellationToken cancellationToken) => effectivePolicies == null
        ? providerGateway.GetProviderOrder(capability)
        : (await effectivePolicies.ResolveForUserAsync(userId, cancellationToken))
            .ApplyProviderAvailability(capability, providerGateway.GetProviderOrder(capability));

    private async Task<TrackMatchDecisionEngine> MatchingEngineAsync(
        Guid userId,
        CancellationToken cancellationToken) => effectivePolicies == null
        ? matcher
        : matcher.WithLocalPriorityWindow(
            (await effectivePolicies.ResolveForUserAsync(userId, cancellationToken)).LocalPreferenceWindow);

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null; error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession found)
        { error = Unauthorized(new { error = "Authentication required" }); return false; }
        if (!found.AllstarrUserId.HasValue)
        { error = StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" }); return false; }
        session = found; return true;
    }

    private sealed record MatchRow(
        TrackMatchState State,
        double? Confidence,
        string SearchText,
        bool HasManualMatch,
        bool HasManualRejection,
        object Value,
        TrackMatchTargetLookup? TargetLookup = null);

    private sealed record ManualAuthorityRow(
        Guid Id,
        string Kind,
        long Revision,
        Guid AuthoritySnapshotId,
        string Reason,
        DateTimeOffset CreatedAt,
        bool Effective,
        string Scope,
        bool CanEdit,
        string? TargetProviderId = null,
        string? TargetExternalId = null);
}
