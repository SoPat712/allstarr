using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class EfManagedFileOwnershipStore
{
    internal async Task StageReferenceAsync(Guid fileId, string key, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireReferenceTransaction();
        var file = await Files.AsNoTracking().SingleAsync(item => item.Id == fileId && item.RemovedAt == null,
            cancellationToken);
        RequireHouseholdFile(file);
        var reference = new ManagedFileReference(Guid.CreateVersion7(), fileId, null, file.ScopeKey, key, now);
        ValidateReference(Map(file)!, reference);
        var existing = await References.SingleOrDefaultAsync(item =>
            item.ManagedFileId == fileId && item.ReferenceKey == key, cancellationToken);
        if (existing == null) References.Add(ToEntity(reference));
        else if (existing.ReleasedAt != null)
        {
            existing.ReleasedAt = null;
            existing.Revision++;
        }
    }

    internal async Task<Guid[]> StageReleaseAsync(string key, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireReferenceTransaction();
        var references = await References.Where(item => item.ReferenceKey == key && item.ReleasedAt == null)
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

    internal async Task<bool> HasStagedActiveReferencesAsync(Guid fileId, CancellationToken cancellationToken)
    {
        RequireReferenceTransaction();
        var saved = await References.Where(item => item.ManagedFileId == fileId && item.ReleasedAt == null)
            .ToArrayAsync(cancellationToken);
        return saved.Any(item => item.ReleasedAt == null) || References.Local.Any(item =>
            item.ManagedFileId == fileId && item.ReleasedAt == null);
    }

    internal async Task<bool> StageKeptRemovalAsync(Guid fileId, Guid recordingId,
        DateTimeOffset now, CancellationToken cancellationToken)
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
                item.CanonicalRecordingId == recordingId, cancellationToken)) return false;

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
