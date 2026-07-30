using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
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
    TrackMatchDecisionEngine matcher) : ControllerBase
{
    public sealed record ResolveTrackMatchRequest(
        string TargetType,
        Guid? LibraryTrackId = null,
        string? ExternalProvider = null,
        string? ExternalId = null,
        string? Reason = null);

    [HttpGet("{externalSnapshotId:guid}/artwork")]
    public async Task<IActionResult> SourceArtwork(
        Guid externalSnapshotId,
        CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == externalSnapshotId &&
            item.TenantId == session!.TenantId &&
            (session.IsAdministrator || item.OwnerUserId == session.AllstarrUserId),
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
                snapshot.TenantId,
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

        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var detail = await trackMatchCommands.GetDetailAsync(
            new TrackMatchActor(tenantId, userId, session.IsAdministrator),
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
        var latestSnapshot = snapshots.FirstOrDefault();
        var sourceMetadata = latestSnapshot == null ? default : Metadata(latestSnapshot.PayloadJson);
        var effectiveDuration = primaryLocal?.DurationMilliseconds ?? sourceMetadata.DurationMilliseconds;
        return Ok(new
        {
            spotifyId,
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
                        ? null
                        : SourceArtworkUrl(latestSnapshot.Id)
                    : LocalArtworkUrl(primaryLocal.BackendItemId),
                sourceArtworkUrl = sourceMetadata.ArtworkUrl == null || latestSnapshot == null
                    ? null
                    : SourceArtworkUrl(latestSnapshot.Id),
                candidateArtworkUrl = primaryLocal?.CoverArtReference == null
                    ? null
                    : LocalArtworkUrl(primaryLocal.BackendItemId),
                isrc = primaryLocal?.Isrc,
                musicBrainzRecordingId = primaryLocal?.MusicBrainzRecordingId
            },
            providerIdentities = identities.Select(item => new
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
        [FromQuery] string? libraryScopeId = null,
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
            !Enum.TryParse<TrackMatchState>(state, true, out _))
            return BadRequest(new { error = "State is not a valid match state" });
        if (sort is not (null or "" or "confidence_desc" or "confidence_asc"))
            return BadRequest(new { error = "Sort is not supported" });

        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var review = await trackMatchCommands.GetReviewDataAsync(
            new TrackMatchActor(tenantId, userId, session.IsAdministrator),
            libraryScopeId,
            search,
            externalSnapshotId,
            cancellationToken: cancellationToken);
        var decisions = review.LatestDecisions.ToDictionary(item => item.ExternalSnapshotId);
        var overrides = review.ActiveOverrides.ToDictionary(item => item.ExternalSnapshotId);
        var library = review.LibraryTracks.ToDictionary(item => item.Id);
        var libraryByCanonical = review.LibraryTracks
            .Where(item => item.CanonicalRecordingId.HasValue)
            .GroupBy(item => item.CanonicalRecordingId!.Value)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(item => item.BackendItemId, StringComparer.Ordinal)
                .First());
        var sourceIdentities = review.ProviderIdentities.ToDictionary(item => item.Id);
        var identities = review.ProviderIdentities
            .GroupBy(item => item.CanonicalRecordingId).ToDictionary(group => group.Key, group => group.ToArray());
        var playableProviders = providerGateway.GetProviderOrder(ProviderCapabilityKind.Streaming)
            .Concat(providerGateway.GetProviderOrder(ProviderCapabilityKind.Download))
            .Select(ExternalTrackPlaybackPolicy.Normalize)
            .ToHashSet(StringComparer.Ordinal);

        var allRows = review.Snapshots
            .GroupBy(snapshot => new
            {
                snapshot.OwnerUserId,
                snapshot.LibraryScopeId,
                SourceIdentity = snapshot.ProviderTrackIdentityId?.ToString("N") ??
                    $"{snapshot.ProviderId}:{snapshot.ExternalIdHash}"
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
                var manual = group
                    .Select(item => overrides.GetValueOrDefault(item.Id))
                    .Where(item => item != null)
                    .OrderByDescending(item => item!.CreatedAt)
                    .FirstOrDefault();
                var sourceIdentity = snapshot.ProviderTrackIdentityId.HasValue
                    ? sourceIdentities.GetValueOrDefault(snapshot.ProviderTrackIdentityId.Value)
                    : null;
                return Row(
                    snapshot,
                    decision,
                    manual,
                    sourceIdentity,
                    library,
                    libraryByCanonical,
                    identities,
                    playableProviders);
            })
            .ToArray();
        var filteredRows = allRows.Where(row => MatchesStateFilter(row.State, state));
        var rows = sort switch
        {
            "confidence_desc" => filteredRows.OrderByDescending(row => row.Confidence).ToArray(),
            "confidence_asc" => filteredRows.OrderBy(row => row.Confidence).ToArray(),
            _ => filteredRows.ToArray()
        };
        var total = rows.Length;
        var items = rows.Skip((page - 1) * pageSize).Take(pageSize).Select(row => row.Value).ToArray();
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
                rejected = allRows.Count(item => item.State == TrackMatchState.Rejected),
                attention = allRows.Count(item => item.State is
                    TrackMatchState.Suggested or TrackMatchState.Ambiguous or TrackMatchState.Rejected)
            },
            pagination = new { page, pageSize, total, totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) }
        }
        );
    }

    [HttpGet("targets/local")]
    public async Task<IActionResult> SearchLocalTargets(
        [FromQuery] string query,
        [FromQuery] string? libraryScopeId = null,
        [FromQuery] Guid? externalSnapshotId = null,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        query = query?.Trim() ?? string.Empty;
        if (query.Length < 2) return BadRequest(new { error = "Enter at least two characters" });
        limit = Math.Clamp(limit, 1, 50);

        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var tracks = await trackMatchCommands.SearchLocalTracksAsync(
            new TrackMatchActor(tenantId, userId, session.IsAdministrator),
            query,
            libraryScopeId,
            limit,
            cancellationToken);
        var source = await ReviewSourceAsync(session!, externalSnapshotId, cancellationToken);
        var scores = source == null
            ? []
            : matcher.ScoreCandidates(source, tracks.Select(ToCandidate))
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
            confidence = scores.GetValueOrDefault(item.Id)?.Confidence,
            components = scores.GetValueOrDefault(item.Id)?.Components,
            reasons = scores.GetValueOrDefault(item.Id)?.Reasons,
            warnings = scores.GetValueOrDefault(item.Id)?.Warnings,
            artworkUrl = item.CoverArtReference == null ? null : LocalArtworkUrl(item.BackendItemId)
        }).OrderByDescending(item => item.confidence).ThenBy(item => item.title).ToArray();
        return Ok(new { tracks = values });
    }

    [HttpGet("targets/provider")]
    public async Task<IActionResult> SearchProviderTargets(
        [FromQuery] string query,
        [FromQuery] string? provider = null,
        [FromQuery] string? libraryScopeId = null,
        [FromQuery] Guid? externalSnapshotId = null,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        query = query?.Trim() ?? string.Empty;
        provider = provider?.Trim() ?? string.Empty;
        if (query.Length < 2) return BadRequest(new { error = "Enter at least two characters" });
        if (provider.Length > 128) return BadRequest(new { error = "The playback provider is invalid" });
        limit = Math.Clamp(limit, 1, 50);

        var playableProviders = providerGateway.GetProviderOrder(ProviderCapabilityKind.Streaming)
            .Concat(providerGateway.GetProviderOrder(ProviderCapabilityKind.Download))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (provider.Length > 0 &&
            !playableProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = "The selected provider is not an installed playback provider" });
        if (playableProviders.Length == 0)
            return Ok(new { tracks = Array.Empty<object>(), providers = playableProviders });

        ProtocolExecutionContext execution;
        try
        {
            execution = await protocolContexts.CreateAsync(
                session!, libraryScopeId, HttpContext.TraceIdentifier, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        }
        var fetchLimit = provider.Length == 0
            ? limit
            : Math.Min(200, limit * playableProviders.Length);
        var songs = (await providerGateway.SearchPlayableSongsAsync(execution, query, fetchLimit))
            .Where(song => !string.IsNullOrWhiteSpace(song.ExternalProvider) &&
                           playableProviders.Contains(song.ExternalProvider, StringComparer.OrdinalIgnoreCase) &&
                           (provider.Length == 0 ||
                            provider.Equals(song.ExternalProvider, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToArray();
        var source = await ReviewSourceAsync(session!, externalSnapshotId, cancellationToken);
        var candidates = songs.Select(song => new
        {
            Song = song,
            Candidate = ToCandidate(
                song,
                session!.TenantId!.Value,
                session.AllstarrUserId!.Value,
                libraryScopeId ?? string.Empty)
        }).ToArray();
        var scores = source == null
            ? []
            : matcher.ScoreCandidates(source, candidates.Select(item => item.Candidate))
                .ToDictionary(item => item.LibraryTrackId);
        var ranked = candidates
            .Select(item => new
            {
                item.Song,
                item.Candidate,
                Confidence = scores.GetValueOrDefault(item.Candidate.LibraryTrackId)?.Confidence
            })
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.Song.ExternalProvider)
            .ThenBy(item => item.Song.Title)
            .ToArray();
        return Ok(new
        {
            tracks = ranked.Select(item =>
            {
                var song = item.Song;
                return new
                {
                    id = song.ExternalId,
                    externalId = song.ExternalId,
                    externalProvider = song.ExternalProvider ?? provider,
                    title = song.Title,
                    artist = song.Artist,
                    album = song.Album,
                    artworkUrl = string.IsNullOrWhiteSpace(song.CoverArtUrl) ||
                                 string.IsNullOrWhiteSpace(song.ExternalId)
                        ? null
                        : ExternalArtworkUrl(song.ExternalProvider ?? provider, song.ExternalId!),
                    durationMilliseconds = song.Duration * 1000,
                    isrc = song.Isrc,
                    confidence = item.Confidence,
                    components = scores.GetValueOrDefault(item.Candidate.LibraryTrackId)?.Components,
                    reasons = scores.GetValueOrDefault(item.Candidate.LibraryTrackId)?.Reasons,
                    warnings = scores.GetValueOrDefault(item.Candidate.LibraryTrackId)?.Warnings
                };
            }),
            providers = playableProviders
        });
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
                session!.TenantId!.Value,
                session.AllstarrUserId!.Value,
                session.IsAdministrator),
            externalSnapshotId,
            new ResolveTrackMatchCommand(
                request.TargetType,
                request.LibraryTrackId,
                ExternalProvider: request.ExternalProvider,
                ExternalId: request.ExternalId,
                Reason: request.Reason),
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
                session!, null, HttpContext.TraceIdentifier, cancellationToken);
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

    private static bool MatchesStateFilter(TrackMatchState state, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        (filter.Equals("attention", StringComparison.OrdinalIgnoreCase) && state is
            TrackMatchState.Suggested or TrackMatchState.Ambiguous or TrackMatchState.Rejected) ||
        (filter.Equals("matched", StringComparison.OrdinalIgnoreCase) && state is TrackMatchState.Accepted or TrackMatchState.Pinned) ||
        state.ToString().Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static string CleanReason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static MatchRow Row(ExternalMetadataSnapshotRecord snapshot, TrackMatchRecord? decision,
        ManualTrackOverrideRecord? manual, ProviderTrackIdentityRecord? sourceIdentity,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> libraryByCanonical,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord[]> identities,
        IReadOnlySet<string> playableProviders)
    {
        var routeCanonicalId = decision?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
        var routeIdentities = routeCanonicalId.HasValue &&
                              identities.TryGetValue(routeCanonicalId.Value, out var canonicalIdentities)
            ? canonicalIdentities
            : [];
        var providerOrder = routeIdentities.Select(item => item.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var indexedLibraryTrackIds = library.Keys.ToHashSet();
        var classification = TrackClassifier.Classify(
            manual,
            decision,
            sourceIdentity,
            routeIdentities,
            providerOrder,
            indexedLibraryTrackIds);
        var state = classification.ReviewState;
        var trackId = classification.LibraryTrackId;
        library.TryGetValue(trackId ?? Guid.Empty, out var track);
        var canonicalId = decision?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId ?? track?.CanonicalRecordingId;
        if (track == null &&
            state is TrackMatchState.Accepted or TrackMatchState.Pinned &&
            canonicalId.HasValue &&
            libraryByCanonical.TryGetValue(canonicalId.Value, out var canonicalTrack) &&
            canonicalTrack.OwnerUserId == snapshot.OwnerUserId &&
            canonicalTrack.LibraryScopeId == snapshot.LibraryScopeId &&
            canonicalTrack.BackendInstanceId == snapshot.BackendInstanceId)
        {
            track = canonicalTrack;
            trackId = canonicalTrack.Id;
        }
        var providerIdentities = canonicalId.HasValue && identities.TryGetValue(canonicalId.Value, out var values)
            ? values.Select(item => new
            {
                providerId = item.ProviderId,
                externalId = item.ExternalId,
                scope = item.Scope.ToString(),
                verification = item.Verification.ToString()
            }).ToArray()
            : [];
        var metadata = Metadata(snapshot.PayloadJson);
        var sourceArtworkUrl = metadata.ArtworkUrl == null
            ? null
            : SourceArtworkUrl(snapshot.Id);
        var candidateArtworkUrl = track?.CoverArtReference == null
            ? null
            : LocalArtworkUrl(track.BackendItemId);
        var value = new
        {
            externalSnapshotId = snapshot.Id,
            providerId = snapshot.ProviderId,
            providerAccountId = snapshot.ProviderAccountId,
            libraryScopeId = snapshot.LibraryScopeId,
            state = state.ToString().ToLowerInvariant(),
            decisionSource = manual != null
                ? "manual_override"
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
            title = metadata.Title,
            artist = metadata.Artist,
            album = metadata.Album,
            artworkUrl = sourceArtworkUrl ?? candidateArtworkUrl,
            sourceArtworkUrl,
            candidateArtworkUrl,
            isrc = metadata.Isrc,
            durationMilliseconds = track?.DurationMilliseconds ?? metadata.DurationMilliseconds,
            durationProvenance = track?.DurationMilliseconds.HasValue == true
                ? track.DurationProvenance ?? track.Protocol
                : metadata.DurationMilliseconds.HasValue ? snapshot.ProviderId : null,
            durationRetrievedAt = track?.DurationMilliseconds.HasValue == true
                ? (DateTimeOffset?)(track.DurationRetrievedAt ?? track.IndexedAt)
                : metadata.DurationMilliseconds.HasValue ? snapshot.RetrievedAt : null,
            localTrack = track == null ? null : new
            {
                id = track.Id,
                backendItemId = track.BackendItemId,
                title = track.Title,
                artist = track.Artist,
                album = track.Album,
                durationMilliseconds = track.DurationMilliseconds,
                durationProvenance = track.DurationProvenance,
                durationRetrievedAt = track.DurationRetrievedAt,
                artworkUrl = candidateArtworkUrl,
                providerIds = ParseObject(track.ProviderIdsJson)
            },
            providerIdentities,
            candidates = ParseCandidates(
                decision?.CandidateResultsJson,
                playableProviders,
                indexedLibraryTrackIds),
            reasons = decision == null && sourceIdentity != null
                ? ["Existing canonical provider identity is available."]
                : ParseArray(decision?.ReasonsJson),
            warnings = ParseArray(decision?.WarningsJson),
            decidedAt = decision?.DecidedAt,
            reviewedAt = manual?.CreatedAt
        };
        return new(state, decision?.Confidence, $"{metadata.Title} {metadata.Artist} {metadata.Album} {snapshot.ProviderId} {track?.Title} {track?.Artist}", value);
    }

    private static (string? Title, string? Artist, string? Album, string? ArtworkUrl, string? Isrc, long? DurationMilliseconds) Metadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = Text(root, "title") ?? Text(root, "Title") ?? Text(root, "name") ?? Text(root, "Name");
            var artist = Text(root, "artist") ?? Text(root, "Artist");
            if (artist == null && (root.TryGetProperty("artists", out var artists) || root.TryGetProperty("Artists", out artists)) && artists.ValueKind == JsonValueKind.Array)
                artist = string.Join(", ", artists.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : Text(item, "name")).Where(item => item != null));
            return (
                title,
                artist,
                Text(root, "album") ?? Text(root, "Album") ?? Text(root, "albumTitle") ?? Text(root, "AlbumTitle"),
                Text(root, "artworkUrl") ?? Text(root, "ArtworkUrl") ?? Text(root, "coverUrl") ?? Text(root, "CoverUrl") ??
                    Text(root, "imageUrl") ?? Text(root, "ImageUrl") ??
                    Text(root, "artworkReference") ?? Text(root, "ArtworkReference"),
                Text(root, "isrc") ?? Text(root, "Isrc") ?? Text(root, "ISRC"),
                DurationMilliseconds(root));
        }
        catch (JsonException) { return (null, null, null, null, null, null); }
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
            item.TenantId == session.TenantId &&
            (session.IsAdministrator || item.OwnerUserId == session.AllstarrUserId),
            cancellationToken);
        if (snapshot == null) return null;
        var identity = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == snapshot.ProviderTrackIdentityId.Value &&
                item.TenantId == snapshot.TenantId,
                cancellationToken)
            : null;
        var canonicalId = identity?.CanonicalRecordingId ?? await db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == snapshot.TenantId &&
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
            null,
            canonicalId);
    }

    private static LocalTrackMatchCandidate ToCandidate(LibraryTrackRecord item) => new(
        item.Id,
        item.TenantId,
        item.OwnerUserId,
        item.BackendInstanceId,
        item.LibraryScopeId,
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
        Guid tenantId,
        Guid ownerUserId,
        string libraryScopeId) => new(
        Guid.CreateVersion7(),
        tenantId,
        ownerUserId,
        string.Empty,
        libraryScopeId,
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
        IsLocal: false);

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

    private static object[] ParseCandidates(
        string? json,
        IReadOnlySet<string> playableProviders,
        IReadOnlySet<Guid> indexedLibraryTrackIds)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray().Select(item =>
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
                    backendItemId = Text(item, "backendItemId") ?? Text(item, "BackendItemId"),
                    confidence = Number(item, "confidence") ?? Number(item, "Confidence"),
                    title = Text(item, "title") ?? Text(item, "Title"),
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

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null; error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession found)
        { error = Unauthorized(new { error = "Authentication required" }); return false; }
        if (!found.TenantId.HasValue || !found.AllstarrUserId.HasValue)
        { error = StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" }); return false; }
        session = found; return true;
    }

    private sealed record MatchRow(TrackMatchState State, double? Confidence, string SearchText, object Value);
}
