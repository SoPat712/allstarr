using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Models.Spotify;
using allstarr.Services;
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
    ITrackMatchRepository trackMatchCommands,
    IEnumerable<IConcreteMetadataService> metadataServices) : ControllerBase
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
                    ? sourceMetadata.ArtworkUrl == null ? null : ExternalArtworkUrl("spotify", spotifyId)
                    : LocalArtworkUrl(primaryLocal.BackendItemId),
                sourceArtworkUrl = sourceMetadata.ArtworkUrl == null
                    ? null
                    : ExternalArtworkUrl("spotify", spotifyId),
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

        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var review = await trackMatchCommands.GetReviewDataAsync(
            new TrackMatchActor(tenantId, userId, session.IsAdministrator),
            libraryScopeId,
            search,
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
                return Row(snapshot, decision, manual, sourceIdentity, library, libraryByCanonical, identities);
            })
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
                matched = allRows.Count(item => item.State is TrackMatchState.Accepted or TrackMatchState.Pinned),
                accepted = allRows.Count(item => item.State is TrackMatchState.Accepted or TrackMatchState.Pinned),
                unresolved = allRows.Count(item => item.State == TrackMatchState.Unresolved),
                review = allRows.Count(item => item.State is TrackMatchState.Suggested or TrackMatchState.Ambiguous),
                rejected = allRows.Count(item => item.State == TrackMatchState.Rejected),
                attention = allRows.Count(item => item.State is TrackMatchState.Unresolved or
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
        var values = tracks.Select(item => new
        {
            item.Id,
            item.BackendItemId,
            item.Title,
            item.Artist,
            item.Album,
            item.DurationMilliseconds,
            item.Isrc,
            artworkUrl = item.CoverArtReference == null ? null : LocalArtworkUrl(item.BackendItemId)
        }).ToArray();
        return Ok(new { tracks = values });
    }

    [HttpGet("targets/provider")]
    public async Task<IActionResult> SearchProviderTargets(
        [FromQuery] string query,
        [FromQuery] string provider,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out _, out var error)) return error!;
        query = query?.Trim() ?? string.Empty;
        provider = provider?.Trim() ?? string.Empty;
        if (query.Length < 2) return BadRequest(new { error = "Enter at least two characters" });
        if (provider.Length is < 2 or > 128) return BadRequest(new { error = "Select a playback provider" });
        limit = Math.Clamp(limit, 1, 50);

        var songs = await PerProviderTrackMatcher.SearchPlayableAsync(
            metadataServices,
            provider,
            query,
            limit,
            cancellationToken);
        return Ok(new
        {
            tracks = songs.Select(song => new
            {
                id = song.ExternalId,
                externalId = song.ExternalId,
                externalProvider = song.ExternalProvider ?? provider,
                song.Title,
                song.Artist,
                song.Album,
                artworkUrl = string.IsNullOrWhiteSpace(song.CoverArtUrl) ||
                             string.IsNullOrWhiteSpace(song.ExternalId)
                    ? null
                    : ExternalArtworkUrl(song.ExternalProvider ?? provider, song.ExternalId!),
                durationMilliseconds = song.Duration * 1000,
                song.Isrc
            })
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
        var result = await trackMatchCommands.RematchSnapshotAsync(
            new TrackMatchActor(
                session!.TenantId!.Value,
                session.AllstarrUserId!.Value,
                session.IsAdministrator),
            externalSnapshotId,
            HttpContext.TraceIdentifier,
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
        (filter.Equals("attention", StringComparison.OrdinalIgnoreCase) && state is TrackMatchState.Unresolved or TrackMatchState.Suggested or TrackMatchState.Ambiguous or TrackMatchState.Rejected) ||
        (filter.Equals("matched", StringComparison.OrdinalIgnoreCase) && state is TrackMatchState.Accepted or TrackMatchState.Pinned) ||
        state.ToString().Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static string CleanReason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static TrackMetadata ToTrackMetadata(
        (string? Title, string? Artist, string? Album, string? ArtworkUrl, string? Isrc) value) => new()
        {
            Title = value.Title,
            Artist = value.Artist,
            Album = value.Album,
            ArtworkUrl = value.ArtworkUrl
        };

    private static MatchRow Row(ExternalMetadataSnapshotRecord snapshot, TrackMatchRecord? decision,
        ManualTrackOverrideRecord? manual, ProviderTrackIdentityRecord? sourceIdentity,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> libraryByCanonical,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord[]> identities)
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
        var classification = TrackClassifier.Classify(
            manual,
            decision,
            sourceIdentity,
            routeIdentities,
            providerOrder,
            library.Keys.ToHashSet());
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
            ? values.Select(item => new { item.ProviderId, item.ExternalId, scope = item.Scope.ToString(), verification = item.Verification.ToString() }).ToArray()
            : [];
        var metadata = Metadata(snapshot.PayloadJson);
        var sourceArtworkUrl = metadata.ArtworkUrl == null || sourceIdentity == null
            ? null
            : ExternalArtworkUrl(sourceIdentity.ProviderId, sourceIdentity.ExternalId);
        var candidateArtworkUrl = track?.CoverArtReference == null
            ? null
            : LocalArtworkUrl(track.BackendItemId);
        var value = new
        {
            externalSnapshotId = snapshot.Id,
            snapshot.ProviderId,
            snapshot.ProviderAccountId,
            snapshot.LibraryScopeId,
            state = state.ToString().ToLowerInvariant(),
            decisionSource = manual != null
                ? "manual_override"
                : decision != null
                    ? "track_match_decision"
                    : sourceIdentity != null
                        ? "canonical_provider_identity"
                        : "unresolved",
            decision?.Confidence,
            decision?.Threshold,
            decision?.DecisionVersion,
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
                track.Id,
                track.BackendItemId,
                track.Title,
                track.Artist,
                track.Album,
                track.DurationMilliseconds,
                track.DurationProvenance,
                track.DurationRetrievedAt,
                artworkUrl = candidateArtworkUrl,
                providerIds = ParseObject(track.ProviderIdsJson)
            },
            providerIdentities,
            candidates = ParseCandidates(decision?.CandidateResultsJson),
            reasons = decision == null && sourceIdentity != null
                ? ["Existing canonical provider identity is available."]
                : ParseArray(decision?.ReasonsJson),
            warnings = ParseArray(decision?.WarningsJson),
            decidedAt = decision?.DecidedAt,
            reviewedAt = manual?.CreatedAt
        };
        return new(state, $"{metadata.Title} {metadata.Artist} {metadata.Album} {snapshot.ProviderId} {track?.Title} {track?.Artist}", value);
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
                providerTrackIds = Element(item, "providerTrackIds", "ProviderTrackIds"),
                components = Element(item, "components", "Components"),
                reasons = Element(item, "reasons", "Reasons"),
                warnings = Element(item, "warnings", "Warnings")
            }).Where(item => item.libraryTrackId != null).Cast<object>().ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static JsonElement? Element(JsonElement root, string camelName, string pascalName) =>
        root.TryGetProperty(camelName, out var value) || root.TryGetProperty(pascalName, out value)
            ? value.Clone()
            : null;

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
