using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackMatchCommandService
{
    public async Task<ExternalMetadataSnapshotRecord> CaptureSnapshotAsync(
        ProtocolExecutionContext context,
        ExternalSnapshotInput input,
        CancellationToken cancellationToken = default)
    {
        var (principal, actor) = PersistenceGuard.Require(context);
        ValidateHash(input.ExternalIdHash, nameof(input.ExternalIdHash));
        ValidateHash(input.PayloadSha256, nameof(input.PayloadSha256));
        if (input.SnapshotVersion <= 0 ||
            string.IsNullOrWhiteSpace(input.ProviderRevision) ||
            string.IsNullOrWhiteSpace(input.ResourceKind))
            throw new ArgumentException(
                "Snapshot version, provider revision, and resource kind are required.",
                nameof(input));
        PersistenceGuard.ValidateSafeJson(input.PayloadJson, nameof(input.PayloadJson));
        var account = await accountResolver.ResolveAsync(new ProviderAccountResolutionRequest(
                principal,
                input.ProviderId,
                "metadata",
                input.ProviderAccountId),
            cancellationToken) ?? throw new UnauthorizedAccessException("The provider account is unavailable.");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var resourceKind = input.ResourceKind.Trim().ToLowerInvariant();
        if (input.ProviderTrackIdentityId.HasValue &&
            (resourceKind != "track" || !await db.ProviderTrackIdentities.AnyAsync(item =>
                item.Id == input.ProviderTrackIdentityId.Value &&
                item.ProviderId == account.Account.ProviderId &&
                item.ResourceKind == ProviderResourceKind.Track &&
                item.CatalogNamespace == "default" &&
                item.ExternalIdHash == input.ExternalIdHash &&
                (item.Scope == ProviderIdentityScope.Catalog && item.ProviderAccountId == null ||
                 item.Scope == ProviderIdentityScope.Account && item.ProviderAccountId == account.Account.Id),
                cancellationToken)))
            throw new UnauthorizedAccessException("The source identity is outside the snapshot scope.");

        var existing = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OwnerUserId == actor.EffectiveUserId &&
            item.ProviderAccountId == account.Account.Id &&
            item.ResourceKind == resourceKind &&
            item.ExternalIdHash == input.ExternalIdHash &&
            item.SnapshotVersion == input.SnapshotVersion,
            cancellationToken);
        if (existing != null)
        {
            if (!existing.PayloadSha256.Equals(input.PayloadSha256, StringComparison.Ordinal) ||
                existing.OwnerUserId != actor.EffectiveUserId ||
                existing.ProviderTrackIdentityId != input.ProviderTrackIdentityId ||
                existing.BackendInstanceId != context.BackendInstanceId ||
                existing.BackendPrincipalId != context.VerifiedBackendPrincipalId ||
                existing.Protocol != context.Protocol.ToString().ToLowerInvariant())
                throw new InvalidOperationException(
                    "The snapshot version already exists with different immutable content or scope.");
            return existing;
        }

        var record = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),

            OwnerUserId = actor.EffectiveUserId!.Value,
            ProviderAccountId = account.Account.Id,
            ProviderTrackIdentityId = input.ProviderTrackIdentityId,
            SourceJobId = input.SourceJobId,

            BackendInstanceId = context.BackendInstanceId,
            BackendPrincipalId = context.VerifiedBackendPrincipalId,
            Protocol = context.Protocol.ToString().ToLowerInvariant(),
            ProviderId = account.Account.ProviderId,
            ResourceKind = resourceKind,
            ExternalIdHash = input.ExternalIdHash,
            SnapshotVersion = input.SnapshotVersion,
            ProviderRevision = input.ProviderRevision.Trim(),
            PayloadJson = input.PayloadJson,
            PayloadSha256 = input.PayloadSha256,
            CorrelationId = context.CorrelationId,
            RetrievedAt = clock.UtcNow
        };
        db.ExternalMetadataSnapshots.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task<ExternalMetadataSnapshotRecord?> FindSnapshotAsync(
        Guid externalSnapshotId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId,
            cancellationToken);
    }

    public async Task<int> EnsureSourceSnapshotsAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        CancellationToken cancellationToken = default)
    {
        var tracks = sourceTracks
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ProviderId) &&
                !string.IsNullOrWhiteSpace(item.ExternalId))
            .GroupBy(
                item => $"{item.ProviderId.Trim().ToLowerInvariant()}:{item.ExternalId.Trim()}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (tracks.Length == 0) return 0;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await db.Users.AsNoTracking()
            .Where(user => user.Enabled)
            .OrderBy(user => user.CreatedAt)
            .ToListAsync(cancellationToken);
        var created = 0;

        foreach (var owner in owners)
        {
            var catalogActor = new ProviderActorContext(

                ProviderActorKind.User,
                owner.Id,
                new ProviderBackendPrincipal(
                    owner.BackendType,
                    owner.BackendInstanceId,
                    owner.BackendPrincipalId));

            foreach (var providerGroup in tracks.GroupBy(
                         item => item.ProviderId.Trim().ToLowerInvariant(),
                         StringComparer.Ordinal))
            {
                var providerId = providerGroup.Key;
                var account = await db.ProviderAccounts.AsNoTracking()
                    .Where(item => item.Enabled && item.ProviderId == providerId &&
                                   (item.OwnerUserId == owner.Id ||
                                    item.OwnerUserId == null))
                    .OrderByDescending(item => item.OwnerUserId == owner.Id)
                    .ThenBy(item => item.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (account == null) continue;

                foreach (var track in providerGroup)
                {
                    var externalId = track.ExternalId.Trim();
                    var externalHash = Hash(externalId);
                    var identity = await db.ProviderTrackIdentities.SingleOrDefaultAsync(item =>
                        item.ProviderId == providerId &&
                        item.ResourceKind == ProviderResourceKind.Track &&
                        item.CatalogNamespace == "default" &&
                        item.Scope == ProviderIdentityScope.Catalog &&
                        item.ExternalIdHash == externalHash,
                        cancellationToken);
                    var now = DateTimeOffset.UtcNow;
                    if (identity == null)
                    {
                        var canonical = CreateProvisionalRecording(owner.Id, now);
                        identity = new ProviderTrackIdentityRecord
                        {
                            Id = Guid.CreateVersion7(),

                            CanonicalRecordingId = canonical.Id,
                            ProviderId = providerId,
                            ResourceKind = ProviderResourceKind.Track,
                            CatalogNamespace = "default",
                            Scope = ProviderIdentityScope.Catalog,
                            ExternalId = externalId,
                            ExternalIdHash = externalHash,
                            Verification = ProviderIdentityVerification.Verified,
                            VerificationMethod = "source-snapshot",
                            DecisionVersion = 1,
                            VerifiedAt = now,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        db.CanonicalRecordings.Add(canonical);
                        db.ProviderTrackIdentities.Add(identity);
                        created++;
                    }

                    var payloadJson = JsonSerializer.Serialize(new
                    {
                        providerId,
                        externalId,
                        track.Title,
                        track.Artist,
                        track.Album,
                        durationMilliseconds = track.DurationMilliseconds is > 0
                            ? track.DurationMilliseconds
                            : null,
                        durationProvenance = track.DurationMilliseconds is > 0 ? providerId : null,
                        durationRetrievedAt = track.DurationMilliseconds is > 0 ? now : (DateTimeOffset?)null,
                        track.Isrc,
                        artworkReference = track.ArtworkReference
                    });
                    var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(item =>
                        item.OwnerUserId == owner.Id &&
                        item.ProviderAccountId == account.Id &&
                        item.ResourceKind == "track" &&
                        item.ExternalIdHash == externalHash &&
                        item.SnapshotVersion == 1,
                        cancellationToken);
                    if (snapshot == null)
                    {
                        snapshot = new ExternalMetadataSnapshotRecord
                        {
                            Id = Guid.CreateVersion7(),

                            OwnerUserId = owner.Id,
                            ProviderAccountId = account.Id,
                            ProviderTrackIdentityId = identity.Id,
                            BackendInstanceId = owner.BackendInstanceId,
                            BackendPrincipalId = owner.BackendPrincipalId,
                            Protocol = owner.BackendType,
                            ProviderId = providerId,
                            ResourceKind = "track",
                            ExternalIdHash = externalHash,
                            SnapshotVersion = 1,
                            ProviderRevision = string.IsNullOrWhiteSpace(track.ProviderRevision)
                                ? "source-v1"
                                : track.ProviderRevision.Trim(),
                            PayloadJson = payloadJson,
                            PayloadSha256 = Hash(payloadJson),
                            CorrelationId = $"source-seed-{providerId}-{externalId}",
                            RetrievedAt = now
                        };
                        db.ExternalMetadataSnapshots.Add(snapshot);
                        db.TrackMatches.Add(new TrackMatchRecord
                        {
                            Id = Guid.CreateVersion7(),

                            OwnerUserId = owner.Id,
                            ExternalSnapshotId = snapshot.Id,
                            CanonicalRecordingId = identity.CanonicalRecordingId,

                            State = TrackMatchState.Unresolved,
                            Confidence = 0,
                            Threshold = 0.88,
                            DecisionVersion = 1,
                            SourceSnapshotVersion = snapshot.SnapshotVersion,
                            MatcherVersion = "unscored",
                            PolicyVersion = "source-seed-v1",
                            CandidateResultsJson = "[]",
                            ReasonsJson = JsonSerializer.Serialize(new[]
                            {
                                "Source track is waiting for a playable match"
                            }),
                            WarningsJson = "[]",
                            CorrelationId = snapshot.CorrelationId,
                            DecidedAt = now
                        });
                        created++;
                    }
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        return created;
    }
}
