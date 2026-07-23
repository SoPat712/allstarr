using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Spotify;

/// <summary>
/// Bridges the v2 Redis mapping cache into the durable v3 identity graph.
/// The compatibility cache remains the fast read path for injected playlists,
/// while Postgres becomes the complete inspectable record.
/// </summary>
public sealed class LegacySpotifyMappingProjector(
    SpotifyMappingService mappings,
    SpotifyPlaylistFetcher playlistFetcher,
    IOptions<SpotifyImportSettings> importSettings,
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableStorageState storageState,
    ILogger<LegacySpotifyMappingProjector> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (storageState.GetSnapshot().Readiness == DurableStorageReadiness.Ready)
                {
                    await ProjectConfiguredSourceTracksAsync(stoppingToken);
                    await ProjectAllAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Legacy Spotify identity projection failed; it will retry");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    internal async Task<int> ProjectAllAsync(CancellationToken cancellationToken)
    {
        var projected = 0;
        for (var skip = 0; ; skip += 200)
        {
            var page = await mappings.GetAllMappingsAsync(skip, 200);
            if (page.Count == 0) break;
            foreach (var mapping in page)
            {
                projected += await ProjectAsync(mapping, cancellationToken) ? 1 : 0;
            }
            if (page.Count < 200) break;
        }

        if (projected > 0)
        {
            logger.LogInformation("Projected {Count} legacy Spotify mappings into Postgres", projected);
        }
        return projected;
    }

    /// <summary>
    /// Projects every configured legacy playlist source entry, including tracks
    /// which have never matched. This keeps the durable identity graph complete
    /// without requiring a manual rematch first.
    /// </summary>
    internal async Task<int> ProjectConfiguredSourceTracksAsync(CancellationToken cancellationToken)
    {
        var projected = 0;
        foreach (var playlist in importSettings.Value.Playlists
                     .Where(item => !string.IsNullOrWhiteSpace(item.Name)))
        {
            var tracks = await playlistFetcher.GetPlaylistTracksAsync(playlist.Name);
            projected += await ProjectSourceTracksAsync(tracks, cancellationToken);
        }

        if (projected > 0)
        {
            logger.LogInformation(
                "Projected {Count} legacy playlist source tracks into Postgres",
                projected);
        }
        return projected;
    }

    /// <summary>
    /// Ensures every source playlist entry is represented in the durable identity
    /// graph, including entries which do not have a playable route yet.
    /// </summary>
    public async Task<int> ProjectSourceTracksAsync(
        IReadOnlyCollection<SpotifyPlaylistTrack> tracks,
        CancellationToken cancellationToken)
    {
        var sourceTracks = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.SpotifyId))
            .GroupBy(track => track.SpotifyId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (sourceTracks.Count == 0) return 0;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await db.Users.AsNoTracking()
            .Where(user => user.Status == PlatformUserStatus.Active)
            .GroupBy(user => user.TenantId)
            .Select(group => group.OrderBy(user => user.CreatedAt).First())
            .ToListAsync(cancellationToken);
        var hashes = sourceTracks.ToDictionary(track => track.SpotifyId, track => Hash(track.SpotifyId), StringComparer.Ordinal);
        var projected = 0;

        foreach (var owner in owners)
        {
            var account = await FindSpotifyAccountAsync(db, owner, cancellationToken);
            if (account == null) continue;
            var backend = await FindBackendIdentityAsync(db, owner, cancellationToken);
            var expectedHashes = hashes.Values.ToList();
            var existing = await db.ProviderTrackIdentities
                .Where(identity => identity.TenantId == owner.TenantId && identity.ProviderId == "spotify" &&
                    identity.ResourceKind == ProviderResourceKind.Track && identity.CatalogNamespace == "default" &&
                    identity.Scope == ProviderIdentityScope.Catalog && expectedHashes.Contains(identity.ExternalIdHash))
                .ToListAsync(cancellationToken);
            var known = existing.ToDictionary(identity => identity.ExternalIdHash, StringComparer.Ordinal);

            foreach (var track in sourceTracks)
            {
                var now = DateTimeOffset.UtcNow;
                if (!known.TryGetValue(hashes[track.SpotifyId], out var identity))
                {
                    var canonical = new CanonicalRecordingRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = owner.TenantId,
                        CreatedByUserId = owner.Id,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    identity = CreateIdentity(owner.TenantId, canonical.Id, "spotify", track.SpotifyId, now);
                    db.CanonicalRecordings.Add(canonical);
                    db.ProviderTrackIdentities.Add(identity);
                    known[identity.ExternalIdHash] = identity;
                    projected++;
                }

                projected += await EnsureSnapshotAndDecisionAsync(
                    db,
                    owner,
                    account,
                    backend,
                    identity,
                    TrackPayload(track),
                    TrackMatchState.Unresolved,
                    null,
                    ["Imported source track is waiting for a playable match."],
                    [],
                    cancellationToken) ? 1 : 0;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return projected;
    }

    private async Task<bool> ProjectAsync(SpotifyTrackMapping mapping, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await db.Users.AsNoTracking()
            .Where(user => user.Status == PlatformUserStatus.Active)
            .GroupBy(user => user.TenantId)
            .Select(group => group.OrderBy(user => user.CreatedAt).First())
            .ToListAsync(cancellationToken);
        var changed = false;

        foreach (var owner in owners)
        {
            var account = await FindSpotifyAccountAsync(db, owner, cancellationToken);
            if (account == null) continue;
            var backend = await FindBackendIdentityAsync(db, owner, cancellationToken);
            var spotifyHash = Hash(mapping.SpotifyId);
            var spotifyIdentity = await db.ProviderTrackIdentities.SingleOrDefaultAsync(identity =>
                identity.TenantId == owner.TenantId && identity.ProviderId == "spotify" &&
                identity.ResourceKind == ProviderResourceKind.Track && identity.CatalogNamespace == "default" &&
                identity.Scope == ProviderIdentityScope.Catalog && identity.ExternalIdHash == spotifyHash,
                cancellationToken);

            CanonicalRecordingRecord canonical;
            if (spotifyIdentity == null)
            {
                var now = DateTimeOffset.UtcNow;
                canonical = new CanonicalRecordingRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = owner.TenantId,
                    CreatedByUserId = owner.Id,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.CanonicalRecordings.Add(canonical);
                db.ProviderTrackIdentities.Add(CreateIdentity(owner.TenantId, canonical.Id, "spotify", mapping.SpotifyId, now));
                changed = true;
            }
            else
            {
                canonical = await db.CanonicalRecordings.SingleAsync(
                    record => record.Id == spotifyIdentity.CanonicalRecordingId,
                    cancellationToken);
            }

            LibraryTrackRecord? selectedLocal = null;
            if (mapping.TargetType.Equals("local", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(mapping.LocalId))
            {
                var localTracks = await db.LibraryTracks.Where(track =>
                    track.TenantId == owner.TenantId && track.BackendItemId == mapping.LocalId)
                    .ToListAsync(cancellationToken);
                selectedLocal = localTracks.FirstOrDefault();
                foreach (var localTrack in localTracks.Where(track => track.CanonicalRecordingId == null))
                {
                    localTrack.CanonicalRecordingId = canonical.Id;
                    localTrack.UpdatedAt = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }

            var playableTargets = mapping.ExternalMappings
                .Concat(!string.IsNullOrWhiteSpace(mapping.ExternalProvider) && !string.IsNullOrWhiteSpace(mapping.ExternalId)
                    ? [new ExternalTrackMapping { Provider = mapping.ExternalProvider, ExternalId = mapping.ExternalId, Source = mapping.Source }]
                    : [])
                .Where(target => !string.IsNullOrWhiteSpace(target.Provider) &&
                                 !string.IsNullOrWhiteSpace(target.ExternalId) &&
                                 ExternalTrackPlaybackPolicy.CanUseForPlayback(target.Provider))
                .DistinctBy(target => $"{target.Provider}:{target.ExternalId}", StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var target in playableTargets)
            {
                var provider = target.Provider.Trim().ToLowerInvariant();
                var externalHash = Hash(target.ExternalId);
                var exists = await db.ProviderTrackIdentities.AsNoTracking().AnyAsync(identity =>
                    identity.TenantId == owner.TenantId && identity.ProviderId == provider &&
                    identity.ResourceKind == ProviderResourceKind.Track && identity.CatalogNamespace == "default" &&
                    identity.Scope == ProviderIdentityScope.Catalog && identity.ExternalIdHash == externalHash,
                    cancellationToken);
                if (exists) continue;
                db.ProviderTrackIdentities.Add(CreateIdentity(
                    owner.TenantId, canonical.Id, provider, target.ExternalId, DateTimeOffset.UtcNow));
                changed = true;
            }

            var state = selectedLocal != null
                ? TrackMatchState.Accepted
                : playableTargets.Length > 0
                    ? TrackMatchState.Suggested
                    : TrackMatchState.Unresolved;
            var reasons = selectedLocal != null
                ? new[] { "Imported local decision matched an indexed library track." }
                : playableTargets.Length > 0
                    ? new[] { "Imported provider route is playable and attached to the canonical recording." }
                    : new[] { "Imported decision has no currently playable target." };
            var warnings = state == TrackMatchState.Unresolved
                ? new[] { "Run rematch or select a local/provider target." }
                : Array.Empty<string>();
            changed |= await EnsureSnapshotAndDecisionAsync(
                db,
                owner,
                account,
                backend,
                spotifyIdentity ?? db.ProviderTrackIdentities.Local.Single(identity =>
                    identity.TenantId == owner.TenantId && identity.ProviderId == "spotify" &&
                    identity.ExternalIdHash == spotifyHash),
                TrackPayload(mapping),
                state,
                selectedLocal,
                reasons,
                warnings,
                cancellationToken);
        }

        if (!changed) return false;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another projector pass or matcher may have inserted the same exact
            // provider identity. The unique constraint is the source of truth.
            return false;
        }
    }

    private static ProviderTrackIdentityRecord CreateIdentity(
        Guid tenantId, Guid canonicalId, string provider, string externalId, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CanonicalRecordingId = canonicalId,
            ProviderId = provider,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = externalId,
            ExternalIdHash = Hash(externalId),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "legacy-projector",
            DecisionVersion = 1,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static Task<ProviderAccountRecord?> FindSpotifyAccountAsync(
        AllstarrDbContext db,
        PlatformUserRecord owner,
        CancellationToken cancellationToken) => db.ProviderAccounts
        .Where(account => account.Enabled && account.ProviderId == "spotify" &&
                          (account.OwnerUserId == owner.Id ||
                           account.TenantId == owner.TenantId && account.OwnerUserId == null ||
                           account.TenantId == null))
        .OrderByDescending(account => account.OwnerUserId == owner.Id)
        .ThenByDescending(account => account.TenantId == owner.TenantId)
        .ThenBy(account => account.CreatedAt)
        .FirstOrDefaultAsync(cancellationToken);

    private static Task<BackendIdentityRecord?> FindBackendIdentityAsync(
        AllstarrDbContext db,
        PlatformUserRecord owner,
        CancellationToken cancellationToken) => db.BackendIdentities.AsNoTracking()
        .Where(identity => identity.TenantId == owner.TenantId && identity.UserId == owner.Id)
        .OrderByDescending(identity => identity.LastSeenAt)
        .FirstOrDefaultAsync(cancellationToken);

    private static async Task<bool> EnsureSnapshotAndDecisionAsync(
        AllstarrDbContext db,
        PlatformUserRecord owner,
        ProviderAccountRecord account,
        BackendIdentityRecord? backend,
        ProviderTrackIdentityRecord spotifyIdentity,
        object payload,
        TrackMatchState state,
        LibraryTrackRecord? localTrack,
        IReadOnlyCollection<string> reasons,
        IReadOnlyCollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(item =>
            item.TenantId == owner.TenantId && item.ProviderAccountId == account.Id &&
            item.ResourceKind == "track" && item.ExternalIdHash == spotifyIdentity.ExternalIdHash &&
            item.SnapshotVersion == 1, cancellationToken);
        var changed = false;
        if (snapshot == null)
        {
            var now = DateTimeOffset.UtcNow;
            snapshot = new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = owner.TenantId,
                OwnerUserId = owner.Id,
                ProviderAccountId = account.Id,
                ProviderTrackIdentityId = spotifyIdentity.Id,
                LibraryScopeId = account.LibraryScopeId ?? "music",
                BackendInstanceId = backend?.BackendInstanceId ?? "legacy-import",
                BackendPrincipalId = backend?.PrincipalId ?? owner.Id.ToString("N"),
                Protocol = backend?.BackendType.ToLowerInvariant() ?? "jellyfin",
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = spotifyIdentity.ExternalIdHash,
                SnapshotVersion = 1,
                ProviderRevision = "legacy-v2",
                PayloadJson = payloadJson,
                PayloadSha256 = Hash(payloadJson),
                CorrelationId = $"legacy-map-{spotifyIdentity.ExternalId}",
                RetrievedAt = now
            };
            db.ExternalMetadataSnapshots.Add(snapshot);
            changed = true;
        }

        var latest = await db.TrackMatches
            .Where(item => item.TenantId == owner.TenantId && item.OwnerUserId == owner.Id &&
                           item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest != null && latest.State == state && latest.LibraryTrackId == localTrack?.Id &&
            latest.CanonicalRecordingId == spotifyIdentity.CanonicalRecordingId)
        {
            return changed;
        }

        db.TrackMatches.Add(new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = owner.TenantId,
            OwnerUserId = owner.Id,
            ExternalSnapshotId = snapshot.Id,
            LibraryTrackId = localTrack?.Id,
            CanonicalRecordingId = spotifyIdentity.CanonicalRecordingId,
            LibraryScopeId = account.LibraryScopeId ?? "music",
            State = state,
            Confidence = state == TrackMatchState.Accepted ? 1 : 0,
            Threshold = 0.9,
            DecisionVersion = (latest?.DecisionVersion ?? 0) + 1,
            PolicyVersion = "legacy-v2-convergence",
            CandidateResultsJson = "[]",
            ReasonsJson = JsonSerializer.Serialize(reasons),
            WarningsJson = JsonSerializer.Serialize(warnings),
            CorrelationId = $"legacy-map-{spotifyIdentity.ExternalId}",
            DecidedAt = DateTimeOffset.UtcNow,
            Revision = 1
        });
        return true;
    }

    private static object TrackPayload(SpotifyPlaylistTrack track) => new
    {
        spotifyId = track.SpotifyId,
        title = track.Title,
        artist = track.PrimaryArtist,
        artists = track.Artists,
        album = track.Album,
        durationMs = track.DurationMs,
        isrc = track.Isrc,
        isExplicit = track.Explicit
    };

    private static object TrackPayload(SpotifyTrackMapping mapping) => new
    {
        spotifyId = mapping.SpotifyId,
        title = mapping.Metadata?.Title,
        artist = mapping.Metadata?.Artist,
        album = mapping.Metadata?.Album,
        durationMs = mapping.Metadata?.DurationMs
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
