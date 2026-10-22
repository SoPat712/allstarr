using System.Data;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public interface ITrackMediaFactsStore
{
    Task RememberMediaFactsAsync(ProviderExecutionContext context, ProviderExternalResourceId track,
        TrackMediaFacts facts, CancellationToken cancellationToken = default);
    Task ApplyStoredMediaFactsAsync(ProviderActorContext actor, IReadOnlyList<Song> songs,
        CancellationToken cancellationToken = default);
}

public sealed partial class TrackIdentityService
{
    private const string MediaSnapshotKind = "track-media";

    public async Task RememberMediaFactsAsync(ProviderExecutionContext execution, ProviderExternalResourceId track,
        TrackMediaFacts facts, CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        ThrowIfUnavailable(execution, cancellationToken);
        execution.RequireResourceOwner(track, ProviderResourceKind.Track);
        if (execution.Actor.EffectiveUserId is not { } user || execution.Actor.BackendPrincipal is not { } backend) return;
        var hash = CanonicalCatalogKeys.Hash(track.Value);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var account = await ValidateExecutionContextAsync(db, execution, cancellationToken);
        var previous = await db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == user &&
            item.ProviderId == track.ProviderId && item.ProviderAccountId == (account == null ? null : (Guid?)account.Id) &&
            item.ResourceKind == MediaSnapshotKind && item.ExternalIdHash == hash)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);
        var revision = $"media:{account?.Revision ?? 0}";
        if (previous?.ProviderRevision == revision)
        {
            try
            {
                if (JsonSerializer.Deserialize<TrackMediaFacts>(previous.PayloadJson) is { } retained)
                    facts = facts.WithFallback(retained);
            }
            catch (JsonException) { }
        }
        var payload = JsonSerializer.Serialize(facts);
        if (previous?.PayloadJson == payload && previous.ProviderRevision == revision) return;
        db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            ProviderAccountId = account?.Id,
            BackendInstanceId = backend.BackendInstanceId,
            BackendPrincipalId = backend.PrincipalId,
            Protocol = backend.BackendType,
            ProviderId = track.ProviderId,
            ResourceKind = MediaSnapshotKind,
            ExternalIdHash = hash,
            SnapshotVersion = (previous?.SnapshotVersion ?? 0) + 1,
            ProviderRevision = revision,
            PayloadJson = payload,
            PayloadSha256 = CanonicalCatalogKeys.Hash(payload),
            CorrelationId = execution.CorrelationId,
            RetrievedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ApplyStoredMediaFactsAsync(ProviderActorContext actor, IReadOnlyList<Song> songs,
        CancellationToken cancellationToken = default)
    {
        if (actor.EffectiveUserId is not { } user || songs.Count == 0) return;
        EnsureStorageReady();
        var candidates = songs.Where(song => !song.IsLocal && !string.IsNullOrWhiteSpace(song.ExternalId) &&
            !string.IsNullOrWhiteSpace(song.ExternalProvider)).Select(song => (Song: song,
                Provider: _registry?.ResolveProviderId(song.ExternalProvider!) ?? song.ExternalProvider!,
                Hash: CanonicalCatalogKeys.Hash(song.ExternalId!))).ToArray();
        var hashes = candidates.Select(item => item.Hash).Distinct().ToArray();
        if (hashes.Length == 0) return;
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateActorAsync(db, actor, cancellationToken);
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user &&
            item.ResourceKind == MediaSnapshotKind && hashes.Contains(item.ExternalIdHash) &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == user) &&
                item.ProviderRevision == "media:" + account.Revision)))
            .ToArrayAsync(cancellationToken);
        var externalIds = candidates.Select(item => item.Song.ExternalId!).Distinct().ToArray();
        var artifacts = await db.ProviderDownloadArtifacts.AsNoTracking().Where(item => item.OwnerUserId == user &&
            item.SourceExternalTrackId != null && externalIds.Contains(item.SourceExternalTrackId) && item.SupersededAt == null &&
            (item.State == allstarr.Core.Downloads.ProviderDownloadArtifactState.Verified || item.State == allstarr.Core.Downloads.ProviderDownloadArtifactState.Placed) &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId && account.Enabled &&
                (account.OwnerUserId == null || account.OwnerUserId == user) && account.Revision == item.SourceAccountRevision)))
            .ToArrayAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            var snapshot = snapshots.Where(item => item.ProviderId == candidate.Provider && item.ExternalIdHash == candidate.Hash)
                .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion).FirstOrDefault();
            var artifact = artifacts.Where(item => item.ProviderId == candidate.Provider && item.SourceExternalTrackId == candidate.Song.ExternalId)
                .OrderByDescending(item => item.VerifiedAt).FirstOrDefault();
            if (artifact != null && (snapshot == null || artifact.VerifiedAt > snapshot.RetrievedAt))
            {
                static int? Positive(int? value) => value is > 0 ? value : null;
                new TrackMediaFacts(new(artifact.MimeType ?? "application/octet-stream", artifact.Container ?? "unknown",
                    artifact.Codec ?? "unknown", Positive(artifact.Bitrate), Positive(artifact.SampleRate), Positive(artifact.BitDepth),
                    Positive(artifact.Channels)), artifact.Length).Apply(candidate.Song);
                continue;
            }
            if (snapshot == null) continue;
            try { JsonSerializer.Deserialize<TrackMediaFacts>(snapshot.PayloadJson)?.Apply(candidate.Song); }
            catch (JsonException) { }
        }
    }
}
