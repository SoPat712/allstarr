using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed record KeptRecordingDeleteRequest(Guid ManagedFileId, long Revision, string ReferenceFingerprint,
    string Confirmation, Guid RequestId);

public sealed record KeptRecordingDeletionPreview(Guid ManagedFileId, long Revision, string ReferenceFingerprint,
    string Title, string Artist, string? Album, int ReferenceCount, int FavoriteReferences, int PlaylistReferences,
    int KeepReferences, int OtherReferences, bool HasLyricsSidecar);

public sealed record KeptDeletionReference(Guid Id, string ReferenceKey, long Revision, DateTimeOffset CreatedAt,
    Guid? FavoriteEventId);
public sealed record KeptDeletionRecording(Guid RecordingId, KeptRecordingMetadata? Metadata, string ProviderId,
    string ExternalId);
public sealed record KeptDeletionSidecar(long Length, string ContentSha256, string DeviceId, string FileId);
public sealed record KeptRecordingDeletion(long Revision, string ReferenceFingerprint, Guid RootId,
    string ContentSha256, long Length, string DeviceId, string FileId, string Protocol, string BackendInstanceId,
    string BackendPrincipalId, KeptDeletionReference[] References, KeptDeletionRecording[] Recordings,
    KeptDeletionSidecar? Sidecar);

public sealed partial class RecordingKeptFiles
{
    public async Task<KeptRecordingDeletionPreview> PreviewDeletionAsync(ProtocolExecutionContext execution,
        Guid managedFileId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequireKeepAdministratorAsync(db, execution, cancellationToken);
        return (await ReadDeletionSnapshotAsync(db, scope.ServiceProvider.GetRequiredService<FilePlacementService>(),
            execution, managedFileId, cancellationToken)).Preview;
    }

    public async Task<DurableJobEnqueueResult> RequestDeletionAsync(ProtocolExecutionContext execution,
        KeptRecordingDeleteRequest request, CancellationToken cancellationToken = default)
    {
        if (jobs == null) throw new InvalidOperationException("The kept deletion queue is unavailable.");
        if (request.RequestId == Guid.Empty || request.Confirmation != "Delete kept copy" ||
            request.ReferenceFingerprint is not { Length: 64 })
            throw new ArgumentException("Confirm Delete kept copy using the current storage preview.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var administrator = await RequireKeepAdministratorAsync(db, execution, cancellationToken);
        var requestKey = $"kept-delete:{request.RequestId:N}";
        var existing = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Type == KeptCleanupJobType &&
            item.OwnerUserId == administrator.Id && item.ScopeKey == DurableJobQueue.CreateUserScopeKey(administrator.Id) &&
            item.ProviderAccountId == null && item.ProviderCapability == null && item.IdempotencyKey == requestKey, cancellationToken);
        if (existing != null)
        {
            var saved = JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(existing.PayloadJson);
            if (saved?.Deletion is not { } accepted || saved.ManagedFileId != request.ManagedFileId ||
                accepted.Revision != request.Revision || accepted.ReferenceFingerprint != request.ReferenceFingerprint ||
                accepted.Protocol != administrator.BackendType || accepted.BackendInstanceId != execution.BackendInstanceId ||
                accepted.BackendPrincipalId != execution.VerifiedBackendPrincipalId)
                throw new InvalidOperationException("This request ID already confirms a different kept deletion.");
            return new(existing.Id, false);
        }
        var snapshot = await ReadDeletionSnapshotAsync(db, scope.ServiceProvider.GetRequiredService<FilePlacementService>(),
            execution, request.ManagedFileId, cancellationToken);
        if (snapshot.Preview.Revision != request.Revision || snapshot.Preview.ReferenceFingerprint != request.ReferenceFingerprint)
            throw new InvalidOperationException("The kept copy or its references changed. Review a fresh deletion preview.");
        var result = await jobs.EnqueueInExistingTransactionAsync(db,
            new DurableJobEnqueueRequest<KeptRecordingCleanupPayload>(KeptCleanupJobType,
                requestKey, new(snapshot.Proof.Recordings[0].RecordingId, request.ManagedFileId,
                    Deletion: snapshot.Proof), administrator.Id, MaxAttempts: 12, CorrelationId: execution.CorrelationId), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<(KeptRecordingDeletionPreview Preview, KeptRecordingDeletion Proof)> ReadDeletionSnapshotAsync(
        AllstarrDbContext db, FilePlacementService files, ProtocolExecutionContext execution, Guid fileId,
        CancellationToken token)
    {
        var file = await db.ManagedFiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == fileId && item.RemovedAt == null, token)
            ?? throw new KeyNotFoundException("The kept copy is no longer available.");
        RequireDeletionOwnership(file);
        if (await db.Set<LegacyMediaAdoptionRecord>().AnyAsync(item => item.ManagedFileId == file.Id, token)) throw Denied();
        await files.VerifyOwnedFileAsync(EfManagedFileOwnershipStore.Map(file)!, token);
        if (string.IsNullOrWhiteSpace(file.FileSystemDeviceId) || string.IsNullOrWhiteSpace(file.FileSystemFileId)) throw Denied();
        var recordings = await ReadDeletionRecordingsAsync(db, file.Id, token);
        var active = await db.ManagedFileReferences.AsNoTracking().Where(item => item.ManagedFileId == file.Id && item.ReleasedAt == null)
            .OrderBy(item => item.Id).ToArrayAsync(token);
        if (active.Length != file.ReferenceCount) throw new InvalidOperationException("The kept references changed. Refresh storage.");
        var stateIds = active.Select(item => HeartStateId(item.ReferenceKey)).OfType<Guid>().ToArray();
        var favorites = await db.Set<FavoriteStateRecord>().AsNoTracking().Where(item => stateIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => (Guid?)item.LastEventId, token);
        var references = active.Select(item => new KeptDeletionReference(item.Id, item.ReferenceKey, item.Revision,
            item.CreatedAt, HeartStateId(item.ReferenceKey) is { } state ? favorites.GetValueOrDefault(state) : null)).ToArray();
        var sidecar = await ReadDeletionSidecarAsync(files, file.TargetRootPath, file.CanonicalPath, token);
        var fingerprint = ReferenceFingerprint(references, recordings, sidecar);
        var display = recordings.Select(item => item.Metadata).FirstOrDefault(item => item != null);
        var hearts = references.Count(item => item.ReferenceKey.StartsWith("heart:", StringComparison.Ordinal));
        var playlists = references.Count(item => item.ReferenceKey.StartsWith("playlist:", StringComparison.Ordinal));
        var keeps = references.Count(item => item.ReferenceKey.StartsWith("keep:user:", StringComparison.Ordinal));
        return (new(file.Id, file.Revision, fingerprint, display?.Title ?? "Kept track",
                display?.Artists.FirstOrDefault() ?? "Unknown artist", display?.Album, active.Length,
                hearts, playlists, keeps, active.Length - hearts - playlists - keeps, sidecar != null),
            new(file.Revision, fingerprint, file.RootId, file.ContentSha256, file.Length,
                file.FileSystemDeviceId!, file.FileSystemFileId!, execution.Principal!.BackendType,
                execution.BackendInstanceId, execution.VerifiedBackendPrincipalId, references, recordings, sidecar));
    }

    private async Task<DurableJobCompletion> DeleteConfirmedAsync(DurableJobClaim claim, CancellationToken token)
    {
        if (jobs == null || !(await jobs.ReauthorizeAsync(claim, token)).Authorized) return DurableJobCompletion.Cancelled();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
        KeptRecordingCleanupPayload payload;
        ManagedFileRecord owned;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var job = await db.Jobs.SingleOrDefaultAsync(item => item.Id == claim.JobId && item.Type == KeptCleanupJobType &&
                item.State == DurableJobState.Running && item.AttemptCount == claim.AttemptNumber && item.CancellationRequestedAt == null &&
                item.OwnerUserId == claim.OwnerUserId && item.ProviderAccountId == null && item.ProviderCapability == null, token);
            if (job == null || !job.IdempotencyKey.StartsWith("kept-delete:", StringComparison.Ordinal) ||
                job.RequestFingerprint != claim.RequestFingerprint || job.PayloadJson != claim.Payload.GetRawText() ||
                job.PolicySnapshotJson != claim.PolicySnapshot.GetRawText()) return DurableJobCompletion.Cancelled();
            payload = JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(job.PayloadJson)!;
            if (payload?.Deletion is not { } proof || payload.ManagedFileId == Guid.Empty || proof.Recordings.Length == 0)
                return DurableJobCompletion.Failure("kept_delete_payload_invalid", "The confirmed deletion target is invalid.");
            if (proof.ReferenceFingerprint != ReferenceFingerprint(proof.References, proof.Recordings, proof.Sidecar))
                return DurableJobCompletion.Failure("kept_delete_payload_invalid", "The saved deletion confirmation changed.");
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == claim.OwnerUserId && item.Enabled && item.IsAdmin &&
                item.BackendType == proof.Protocol && item.BackendInstanceId == proof.BackendInstanceId &&
                item.BackendPrincipalId == proof.BackendPrincipalId, token);
            if (user == null) return DurableJobCompletion.Cancelled();
            var file = await db.ManagedFiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == payload.ManagedFileId, token)
                ?? throw new InvalidOperationException("The confirmed kept copy is no longer available.");
            RequireDeletionOwnership(file);
            owned = EfManagedFileOwnershipStore.Map(file)!;
            if (file.RemovedAt == null)
            {
                if (file.Revision != proof.Revision || file.ContentSha256 != proof.ContentSha256 || file.Length != proof.Length ||
                    file.FileSystemDeviceId != proof.DeviceId || file.FileSystemFileId != proof.FileId || proof.RootId != file.RootId)
                    throw new InvalidOperationException("The kept copy changed after confirmation.");
                var active = await db.ManagedFileReferences.AsNoTracking().Where(item => item.ManagedFileId == file.Id && item.ReleasedAt == null)
                    .OrderBy(item => item.Id).ToArrayAsync(token);
                if (active.Length != proof.References.Length || active.Where((item, index) => item.Id != proof.References[index].Id ||
                    item.ReferenceKey != proof.References[index].ReferenceKey || item.Revision != proof.References[index].Revision ||
                    item.CreatedAt != proof.References[index].CreatedAt).Any())
                    throw new InvalidOperationException("The kept references changed after confirmation.");
                var heartStates = proof.References.Where(item => item.FavoriteEventId != null)
                    .Select(item => HeartStateId(item.ReferenceKey)).OfType<Guid>().ToArray();
                var generations = await db.Set<FavoriteStateRecord>().AsNoTracking().Where(item => heartStates.Contains(item.Id))
                    .ToDictionaryAsync(item => item.Id, item => item.LastEventId, token);
                if (proof.References.Any(item => item.FavoriteEventId is { } expected &&
                    (HeartStateId(item.ReferenceKey) is not { } state || !generations.TryGetValue(state, out var current) || current != expected)))
                    throw new InvalidOperationException("A favorite changed after confirmation.");
                var recordings = await ReadDeletionRecordingsAsync(db, file.Id, token);
                if (JsonSerializer.Serialize(recordings) != JsonSerializer.Serialize(proof.Recordings))
                    throw new InvalidOperationException("The kept publication changed after confirmation.");
                await files.VerifyOwnedFileAsync(owned, token);
                var now = clock.UtcNow;
                var store = new EfManagedFileOwnershipStore(db);
                await store.StageReleaseReferencesAsync(active.Select(item => item.Id).ToArray(), now, token);
                await db.SaveChangesAsync(token);
                if (!await store.StageKeptRemovalAsync(file.Id, payload.RecordingId, now, token))
                    throw new InvalidOperationException("The kept references changed during deletion.");
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = user.Id,
                    Category = "kept-recording",
                    Action = "deleted-copy",
                    Outcome = "success",
                    SubjectType = "managed-file",
                    SubjectId = file.Id.ToString("D"),
                    CorrelationId = claim.CorrelationId,
                    CreatedAt = now,
                    DetailsJson = JsonSerializer.Serialize(new { managedFileId = file.Id, referenceCount = active.Length })
                });
            }
            else if (!await DeletionEvidenceMatchesAsync(db, job, payload, file, token))
                throw new InvalidOperationException("This job did not revoke the confirmed kept copy.");
            job.Revision++;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return DurableJobCompletion.Failure("kept_delete_evidence_changed", "The kept copy or administrator access changed. Review storage before trying again.");
        }
        var sidecarChanged = false;
        if (payload.Deletion!.Sidecar != null)
        {
            var current = await ReadDeletionSidecarAsync(files, owned.TargetRootPath, owned.CanonicalPath, token);
            if (current == payload.Deletion.Sidecar)
            {
                var path = Path.ChangeExtension(owned.CanonicalPath, ".lrc");
                // A second complete identity/hash check prevents a replaced sidecar from being adopted by a retry.
                if (await ReadDeletionSidecarAsync(files, owned.TargetRootPath, owned.CanonicalPath, token) == current)
                    File.Delete(path);
                else sidecarChanged = true;
            }
            else sidecarChanged = current != null || File.Exists(Path.ChangeExtension(owned.CanonicalPath, ".lrc"));
        }
        var removed = await scope.ServiceProvider.GetRequiredService<ManagedFileRemovalService>()
            .ResumeUnpublishedRemovalAsync(payload.ManagedFileId, ManagedFileScopeKey.Create(null, placement.RootId), token);
        if (!removed) return DurableJobCompletion.Retry("kept_delete_physical_pending", "The kept route was revoked. Physical cleanup needs another attempt.");
        return sidecarChanged ? new(DurableJobCompletionKind.Succeeded, "kept_delete_lyrics_changed",
            "The kept copy was deleted. Changed lyrics were left in storage.") : DurableJobCompletion.Success();
    }

    private void RequireDeletionOwnership(ManagedFileOwnershipEntity file)
    {
        if (!file.IsManaged || file.OwnerUserId != null || file.SourceJobId != null || file.RootId != placement.RootId ||
            file.ScopeKey != ManagedFileScopeKey.Create(null, placement.RootId) ||
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(file.TargetRootPath)) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(placement.RootPath)))
            throw Denied();
        for (var directory = new DirectoryInfo(file.TargetRootPath); directory != null; directory = directory.Parent)
            if (directory.LinkTarget != null) throw Denied();
    }

    private static KeptRecordingMetadata? ReadDeletionMetadata(string? json)
    {
        if (json == null) return null;
        try
        {
            var value = JsonSerializer.Deserialize<KeptRecordingMetadata>(json);
            return value != null && ValidMetadata(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<KeptDeletionRecording[]> ReadDeletionRecordingsAsync(AllstarrDbContext db, Guid fileId, CancellationToken token)
    {
        var routes = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                            join artifact in db.ProviderDownloadArtifacts.AsNoTracking() on route.SourceArtifactId equals artifact.Id
                            where route.ManagedFileId == fileId
                            orderby route.CanonicalRecordingId
                            select new { route.CanonicalRecordingId, route.DisplayMetadataJson, artifact.ProviderId, artifact.SourceExternalTrackId })
            .ToArrayAsync(token);
        if (routes.Length == 0 || routes.Any(item => string.IsNullOrWhiteSpace(item.SourceExternalTrackId))) throw Denied();
        return routes.Select(item => new KeptDeletionRecording(item.CanonicalRecordingId,
            ReadDeletionMetadata(item.DisplayMetadataJson), item.ProviderId, item.SourceExternalTrackId!)).ToArray();
    }

    private static Guid? HeartStateId(string key) => key.StartsWith("heart:", StringComparison.Ordinal) &&
        Guid.TryParse(key[6..], out var id) ? id : null;

    private static string ReferenceFingerprint(KeptDeletionReference[] references, KeptDeletionRecording[] recordings, KeptDeletionSidecar? sidecar) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { References = references, Recordings = recordings, Sidecar = sidecar })));

    private static async Task<KeptDeletionSidecar?> ReadDeletionSidecarAsync(FilePlacementService files, string rootPath,
        string audioPath, CancellationToken token)
    {
        var path = Path.ChangeExtension(audioPath, ".lrc");
        if (!File.Exists(path)) return null;
        try
        {
            var length = new FileInfo(path).Length;
            if (length > 1024 * 1024) return null;
            var before = files.InspectLegacyFile(rootPath, path, length);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!new PhysicalManagedFileOperations().TryGetFileIdentity(stream, out var opened) ||
                opened.DeviceId != before.DeviceId || opened.FileId != before.FileId) return null;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            var after = files.InspectLegacyFile(rootPath, path, length);
            return before.DeviceId == after.DeviceId && before.FileId == after.FileId
                ? new(length, hash, before.DeviceId, before.FileId) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<bool> DeletionEvidenceMatchesAsync(AllstarrDbContext db, DurableJobRecord job,
        KeptRecordingCleanupPayload payload, ManagedFileOwnershipEntity file, CancellationToken token)
    {
        var proof = payload.Deletion;
        if (proof == null || job.Type != KeptCleanupJobType || !job.IdempotencyKey.StartsWith("kept-delete:", StringComparison.Ordinal) ||
            proof.ReferenceFingerprint != ReferenceFingerprint(proof.References, proof.Recordings, proof.Sidecar) ||
            payload.ManagedFileId != file.Id || !file.IsManaged || file.OwnerUserId != null || file.SourceJobId != null ||
            file.RemovedAt == null || file.RemovedAt < job.CreatedAt || file.ReferenceCount != 0 || file.RootId != proof.RootId ||
            file.ScopeKey != ManagedFileScopeKey.Create(null, proof.RootId) || file.ContentSha256 != proof.ContentSha256 ||
            file.Length != proof.Length || file.FileSystemDeviceId != proof.DeviceId || file.FileSystemFileId != proof.FileId ||
            file.Revision != proof.Revision + proof.References.Length + 1) return false;
        var references = await db.ManagedFileReferences.AsNoTracking().Where(item => item.ManagedFileId == file.Id).ToArrayAsync(token);
        return references.All(item => item.ReleasedAt != null) && proof.References.All(expected => references.Any(item =>
            item.Id == expected.Id && item.ReferenceKey == expected.ReferenceKey && item.Revision == expected.Revision + 1 &&
            item.CreatedAt == expected.CreatedAt && item.ReleasedAt == file.RemovedAt));
    }

    private static async Task<IReadOnlyList<(KeptRecordingDeletion Proof, DateTimeOffset RemovedAt, Guid JobId)>> ReadDeletionEvidenceAsync(
        AllstarrDbContext db, CancellationToken token)
    {
        var removed = await db.ManagedFiles.AsNoTracking().Where(item => item.RemovedAt != null && item.IsManaged &&
            item.OwnerUserId == null && item.SourceJobId == null).ToDictionaryAsync(item => item.Id, token);
        if (removed.Count == 0) return [];
        var jobs = await db.Jobs.AsNoTracking().Where(item => item.Type == KeptCleanupJobType &&
            item.IdempotencyKey.StartsWith("kept-delete:")).OrderByDescending(item => item.CreatedAt).ToArrayAsync(token);
        var result = new List<(KeptRecordingDeletion, DateTimeOffset, Guid)>();
        foreach (var job in jobs)
        {
            KeptRecordingCleanupPayload? payload;
            try { payload = JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(job.PayloadJson); }
            catch (JsonException) { continue; }
            if (payload?.Deletion == null || !removed.TryGetValue(payload.ManagedFileId, out var file) ||
                !await DeletionEvidenceMatchesAsync(db, job, payload, file, token)) continue;
            result.Add((payload.Deletion, file.RemovedAt!.Value, job.Id));
        }
        return result;
    }

    internal static async Task<bool> AutomaticRetentionSuppressedAsync(AllstarrDbContext db, string referenceKey,
        Guid? favoriteEventId = null, CancellationToken cancellationToken = default)
    {
        foreach (var (proof, _, _) in await ReadDeletionEvidenceAsync(db, cancellationToken))
            if (proof.References.Any(item => item.ReferenceKey == referenceKey &&
                (favoriteEventId == null || item.FavoriteEventId == favoriteEventId))) return true;
        return false;
    }

    internal static async Task<IReadOnlySet<string>> SuppressedRetentionKeysAsync(AllstarrDbContext db, CancellationToken token) =>
        (await ReadDeletionEvidenceAsync(db, token)).SelectMany(item => item.Proof.References)
            .Select(item => item.ReferenceKey).ToHashSet(StringComparer.Ordinal);

    private static async Task<bool> PublicationPredatesDeletionAsync(AllstarrDbContext db, Guid recordingId,
        DurableJobRecord request, CancellationToken token)
    {
        var deletions = await ReadDeletionEvidenceAsync(db, token);
        if (deletions.Count == 0) return false;
        var target = await db.CanonicalRecordings.AsNoTracking().Where(item => item.Id == recordingId)
            .Select(item => item.MergedIntoId ?? item.Id).SingleOrDefaultAsync(token);
        var aliases = await db.CanonicalRecordings.AsNoTracking().Where(item => item.Id == target || item.MergedIntoId == target)
            .Select(item => item.Id).ToArrayAsync(token);
        return deletions.Any(item =>
            (item.RemovedAt > request.CreatedAt || item.RemovedAt == request.CreatedAt && request.Id.CompareTo(item.JobId) < 0) &&
            item.Proof.Recordings.Any(recording => aliases.Contains(recording.RecordingId)));
    }

    private static async Task<string?> DeletedDisplaySnapshotAsync(AllstarrDbContext db, Guid recordingId, CancellationToken token)
    {
        var aliases = await db.CanonicalRecordings.AsNoTracking().Where(item => item.Id == recordingId || item.MergedIntoId == recordingId)
            .Select(item => item.Id).ToArrayAsync(token);
        foreach (var (proof, _, _) in await ReadDeletionEvidenceAsync(db, token))
        {
            var metadata = proof.Recordings.Where(item => aliases.Contains(item.RecordingId))
                .OrderByDescending(item => item.RecordingId == recordingId).FirstOrDefault()?.Metadata;
            if (metadata != null) return JsonSerializer.Serialize(metadata);
        }
        return null;
    }
}
