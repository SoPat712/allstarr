using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
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
                    await ProjectAllAsync(stoppingToken);
                    await ProjectConfiguredSourceTracksAsync(stoppingToken);
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
        var sourceIds = tracks
            .Select(track => track.SpotifyId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sourceIds.Count == 0) return 0;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await db.Users.AsNoTracking()
            .Where(user => user.Status == PlatformUserStatus.Active)
            .GroupBy(user => user.TenantId)
            .Select(group => group.OrderBy(user => user.CreatedAt).First())
            .ToListAsync(cancellationToken);
        var hashes = sourceIds.ToDictionary(id => id, Hash, StringComparer.Ordinal);
        var projected = 0;

        foreach (var owner in owners)
        {
            var expectedHashes = hashes.Values.ToList();
            var existing = await db.ProviderTrackIdentities.AsNoTracking()
                .Where(identity => identity.TenantId == owner.TenantId && identity.ProviderId == "spotify" &&
                    identity.ResourceKind == ProviderResourceKind.Track && identity.CatalogNamespace == "default" &&
                    identity.Scope == ProviderIdentityScope.Catalog && expectedHashes.Contains(identity.ExternalIdHash))
                .Select(identity => identity.ExternalIdHash)
                .ToListAsync(cancellationToken);
            var known = existing.ToHashSet(StringComparer.Ordinal);

            foreach (var sourceId in sourceIds.Where(id => !known.Contains(hashes[id])))
            {
                var now = DateTimeOffset.UtcNow;
                var canonical = new CanonicalRecordingRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = owner.TenantId,
                    CreatedByUserId = owner.Id,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.CanonicalRecordings.Add(canonical);
                db.ProviderTrackIdentities.Add(CreateIdentity(owner.TenantId, canonical.Id, "spotify", sourceId, now));
                projected++;
            }
        }

        if (projected == 0) return 0;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return projected;
        }
        catch (DbUpdateException exception)
        {
            logger.LogDebug(exception, "A concurrent matcher projected one or more Spotify source identities");
            return 0;
        }
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

            if (mapping.TargetType.Equals("local", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(mapping.LocalId))
            {
                var localTracks = await db.LibraryTracks.Where(track =>
                    track.TenantId == owner.TenantId && track.BackendItemId == mapping.LocalId)
                    .ToListAsync(cancellationToken);
                foreach (var localTrack in localTracks.Where(track => track.CanonicalRecordingId == null))
                {
                    localTrack.CanonicalRecordingId = canonical.Id;
                    localTrack.UpdatedAt = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }

            foreach (var target in mapping.ExternalMappings
                         .Where(target => !string.IsNullOrWhiteSpace(target.Provider) && !string.IsNullOrWhiteSpace(target.ExternalId)))
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

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
