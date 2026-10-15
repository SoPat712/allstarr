using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class EfManagedFileOwnershipStore
{
    internal Task StageReferenceAsync(Guid fileId, string key, DateTimeOffset now,
        CancellationToken cancellationToken) => StageReferencesAsync(
            new Dictionary<string, Guid>(StringComparer.Ordinal) { [key] = fileId }, now, cancellationToken);

    internal async Task StageReferencesAsync(IReadOnlyDictionary<string, Guid> desired, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireReferenceTransaction();
        if (desired.Count == 0) return;
        var fileIds = desired.Values.Distinct().ToArray();
        var files = await Files.AsNoTracking().Where(item => EF.Parameter(fileIds).Contains(item.Id) && item.RemovedAt == null)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var fileId in fileIds)
        {
            if (!files.TryGetValue(fileId, out var file)) throw new InvalidOperationException("Managed file not found.");
            RequireHouseholdFile(file);
        }
        var keys = desired.Keys.ToArray();
        var saved = await References.Where(item => EF.Parameter(fileIds).Contains(item.ManagedFileId) && EF.Parameter(keys).Contains(item.ReferenceKey))
            .ToArrayAsync(cancellationToken);
        var existing = saved.Concat(References.Local).DistinctBy(item => item.Id)
            .ToDictionary(item => (item.ManagedFileId, item.ReferenceKey));
        var additions = new List<ManagedFileReferenceEntity>();
        foreach (var (key, fileId) in desired)
        {
            var file = files[fileId];
            var reference = new ManagedFileReference(Guid.CreateVersion7(), fileId, null, file.ScopeKey, key, now);
            ValidateReference(Map(file)!, reference);
            if (!existing.TryGetValue((fileId, key), out var current)) additions.Add(ToEntity(reference));
            else if (current.ReleasedAt != null)
            {
                current.ReleasedAt = null;
                current.Revision++;
            }
        }
        References.AddRange(additions);
    }

    internal async Task<Guid[]> StageReleaseAsync(string key, DateTimeOffset now,
        CancellationToken cancellationToken, Guid? fileId = null)
    {
        RequireReferenceTransaction();
        var references = await References.Where(item => item.ReferenceKey == key && item.ReleasedAt == null &&
                (fileId == null || item.ManagedFileId == fileId))
            .ToArrayAsync(cancellationToken);
        foreach (var reference in references)
        {
            var file = await Files.AsNoTracking().SingleAsync(item => item.Id == reference.ManagedFileId &&
                item.RemovedAt == null, cancellationToken);
            RequireHouseholdFile(file);
            ValidateReference(Map(file)!, new(reference.Id, file.Id, reference.OwnerUserId,
                reference.ScopeKey, reference.ReferenceKey, reference.CreatedAt));
            reference.ReleasedAt = now;
            reference.Revision++;
        }
        return references.Select(item => item.ManagedFileId).Distinct().ToArray();
    }

    internal async Task<Guid[]> StageReleaseReferencesAsync(IReadOnlyCollection<Guid> referenceIds, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireReferenceTransaction();
        if (referenceIds.Count == 0) return [];
        var rows = await References.Where(item => EF.Parameter(referenceIds).Contains(item.Id) && item.ReleasedAt == null)
            .ToArrayAsync(cancellationToken);
        var fileIds = rows.Select(item => item.ManagedFileId).Distinct().ToArray();
        var files = await Files.AsNoTracking().Where(item => EF.Parameter(fileIds).Contains(item.Id) && item.RemovedAt == null)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var reference in rows)
        {
            if (!files.TryGetValue(reference.ManagedFileId, out var file)) throw new InvalidOperationException("Managed file not found.");
            RequireHouseholdFile(file);
            ValidateReference(Map(file)!, new(reference.Id, file.Id, reference.OwnerUserId,
                reference.ScopeKey, reference.ReferenceKey, reference.CreatedAt));
        }
        foreach (var reference in rows)
        {
            reference.ReleasedAt = now;
            reference.Revision++;
        }
        return fileIds;
    }

    internal async Task<bool> HasStagedActiveReferencesAsync(Guid fileId, CancellationToken cancellationToken)
    {
        RequireReferenceTransaction();
        var saved = await References.Where(item => item.ManagedFileId == fileId && item.ReleasedAt == null)
            .ToArrayAsync(cancellationToken);
        return saved.Any(item => item.ReleasedAt == null) || References.Local.Any(item =>
            item.ManagedFileId == fileId && item.ReleasedAt == null);
    }

    internal async Task<bool> StageKeptRemovalAsync(Guid fileId, Guid recordingId,
        DateTimeOffset now, CancellationToken cancellationToken, bool allowUnpublished = false)
    {
        RequireReferenceTransaction();
        var file = await Files.AsNoTracking().SingleOrDefaultAsync(item => item.Id == fileId &&
            item.RemovedAt == null, cancellationToken);
        if (file == null) return false;
        RequireHouseholdFile(file);
        if (file.ReferenceCount != 0 || await References.AsNoTracking().AnyAsync(item =>
                item.ManagedFileId == fileId && item.ReleasedAt == null, cancellationToken)) return false;
        var routes = dbContext.Set<RecordingKeptRouteEntity>();
        if (!await routes.AnyAsync(item => item.ManagedFileId == fileId &&
                item.CanonicalRecordingId == recordingId, cancellationToken) &&
            (!allowUnpublished || await routes.AnyAsync(item => item.ManagedFileId == fileId, cancellationToken)))
            return false;

        // A content-deduplicated file can serve several recordings. Actual file
        // references protect all of them; remove their zero-interest routes together.
        await routes.Where(item => item.ManagedFileId == fileId).ExecuteDeleteAsync(cancellationToken);
        var updated = await Files.Where(item => item.Id == fileId && item.Revision == file.Revision &&
            item.RemovedAt == null && item.ReferenceCount == 0).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.RemovedAt, now)
                .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
        if (updated != 1) throw new DbUpdateConcurrencyException("Kept ownership changed during cleanup.");
        return true;
    }

    private void RequireReferenceTransaction()
    {
        if (dbContext.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Reference staging requires the caller's writer transaction.");
    }

    private static void RequireHouseholdFile(ManagedFileOwnershipEntity file)
    {
        if (!file.IsManaged || file.OwnerUserId != null || file.SourceJobId != null || file.RootId == Guid.Empty ||
            file.ScopeKey != ManagedFileScopeKey.Create(null, file.RootId))
            throw new UnauthorizedAccessException("The file is outside household kept ownership.");
    }
}
