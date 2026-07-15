using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed class EfManagedFileOwnershipStore(AllstarrDbContext dbContext) : IManagedFileOwnershipStore, IManagedFileRemovalStore
{
    private DbSet<ManagedFileOwnershipEntity> Files => dbContext.Set<ManagedFileOwnershipEntity>();
    private DbSet<ManagedFileReferenceEntity> References => dbContext.Set<ManagedFileReferenceEntity>();

    public async Task<ManagedFileRecord?> FindByPathAsync(string canonicalPath, CancellationToken cancellationToken) =>
        Map(await Files.AsNoTracking().SingleOrDefaultAsync(item => item.CanonicalPath == canonicalPath && item.RemovedAt == null, cancellationToken));

    public async Task<ManagedFileRecord?> FindCompatibleAsync(Guid rootId, string contentSha256, string scopeKey, CancellationToken cancellationToken) =>
        Map(await Files.AsNoTracking().OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(item =>
            item.RootId == rootId && item.ContentSha256 == contentSha256 && item.ScopeKey == scopeKey && item.RemovedAt == null, cancellationToken));

    public async Task<ManagedFileRecord> AddAsync(
        ManagedFileRecord record,
        ManagedFileReference reference,
        CancellationToken cancellationToken)
    {
        if (record.ReferenceCount != 1)
            throw new InvalidOperationException("A new managed file must begin with exactly one durable reference.");
        ValidateReference(record, reference);
        var entity = ToEntity(record);
        Files.Add(entity);
        References.Add(ToEntity(reference));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return Map(await Files.AsNoTracking().SingleAsync(item => item.Id == record.Id, cancellationToken))!;
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            var winner = await Files.AsNoTracking().SingleOrDefaultAsync(item =>
                item.CanonicalPath == record.CanonicalPath && item.RemovedAt == null, cancellationToken);
            if (winner is null || winner.RootId != record.RootId || winner.ContentSha256 != record.ContentSha256 || winner.ScopeKey != record.ScopeKey)
                throw;
            return await AddReferenceAsync(winner.Id, reference with { ManagedFileId = winner.Id }, cancellationToken);
        }
    }

    public async Task<ManagedFileRecord> AddReferenceAsync(
        Guid id,
        ManagedFileReference reference,
        CancellationToken cancellationToken)
    {
        if (reference.ManagedFileId != id)
            throw new InvalidOperationException("Managed-file references must name their owning file.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entity = await Files.SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken)
                ?? throw new KeyNotFoundException("Managed file not found.");
            ValidateReference(Map(entity)!, reference);
            var existing = await References.SingleOrDefaultAsync(item =>
                item.ManagedFileId == id && item.ReferenceKey == reference.ReferenceKey, cancellationToken);
            if (existing is not null && existing.ReleasedAt is null) return Map(entity)!;
            if (existing is null)
                References.Add(ToEntity(reference));
            else
            {
                existing.ReleasedAt = null;
                existing.Revision++;
            }
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                return Map(await Files.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken))!;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                dbContext.ChangeTracker.Clear();
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // A concurrent retry can win the unique reference key. Reload it;
                // its successful transaction already adjusted the count.
                dbContext.ChangeTracker.Clear();
                var winner = await References.AsNoTracking().SingleOrDefaultAsync(item =>
                    item.ManagedFileId == id && item.ReferenceKey == reference.ReferenceKey &&
                    item.ReleasedAt == null, cancellationToken);
                if (winner is not null)
                    return Map(await Files.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken))!;
            }
        }
        throw new DbUpdateConcurrencyException("The managed-file reference changed concurrently.");
    }

    public async Task<ManagedFileRecord> ReleaseReferenceAsync(
        Guid id,
        string referenceKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(referenceKey))
            throw new ArgumentException("A managed-file reference key is required.", nameof(referenceKey));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entity = await Files.SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken)
                ?? throw new KeyNotFoundException("Managed file not found.");
            var reference = await References.SingleOrDefaultAsync(item =>
                item.ManagedFileId == id && item.ReferenceKey == referenceKey, cancellationToken)
                ?? throw new KeyNotFoundException("Managed-file reference not found.");
            if (reference.ReleasedAt is not null) return Map(entity)!;
            reference.ReleasedAt = DateTimeOffset.UtcNow;
            reference.Revision++;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                return Map(await Files.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken))!;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                dbContext.ChangeTracker.Clear();
            }
        }
        throw new DbUpdateConcurrencyException("The managed-file reference changed concurrently.");
    }

    public async Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Map(await Files.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken));

    public async Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var entity = await Files.SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Managed file not found.");
        if (entity.ReferenceCount > 1 || !entity.IsManaged)
            throw new InvalidOperationException("Managed-file ownership changed before removal completed.");
        if (entity.ReferenceCount == 1)
        {
            var active = await References.Where(item => item.ManagedFileId == id && item.ReleasedAt == null)
                .ToListAsync(cancellationToken);
            if (active.Count != 1)
                throw new InvalidOperationException("Managed-file references are inconsistent.");
            active[0].ReleasedAt = DateTimeOffset.UtcNow;
            active[0].Revision++;
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            entity = await Files.SingleAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken);
        }
        if (entity.ReferenceCount != 0)
            throw new InvalidOperationException("Managed-file references are inconsistent.");
        entity.RemovedAt = DateTimeOffset.UtcNow;
        entity.Revision++;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static ManagedFileOwnershipEntity ToEntity(ManagedFileRecord record) => new()
    {
        Id = record.Id,
        RootId = record.RootId,
        TargetRootPath = string.IsNullOrWhiteSpace(record.TargetRootPath)
            ? throw new InvalidOperationException("Managed files require their configured target root.")
            : record.TargetRootPath,
        CanonicalPath = record.CanonicalPath,
        ContentSha256 = record.ContentSha256,
        Length = record.Length,
        FileSystemDeviceId = record.FileSystemDeviceId,
        FileSystemFileId = record.FileSystemFileId,
        FileSystemLinkCount = record.FileSystemLinkCount,
        PlacementMethod = record.PlacementMethod,
        TenantId = record.TenantId ?? throw new InvalidOperationException("Managed files require tenant ownership."),
        OwnerUserId = record.OwnerUserId,
        LibraryScopeId = record.LibraryScopeId,
        SourceJobId = record.SourceJobId,
        ScopeKey = record.ScopeKey,
        ReferenceCount = record.ReferenceCount,
        IsManaged = record.IsManaged,
        CreatedAt = record.CreatedAt,
        Revision = 1
    };

    private static ManagedFileReferenceEntity ToEntity(ManagedFileReference reference) => new()
    {
        Id = reference.Id,
        ManagedFileId = reference.ManagedFileId,
        TenantId = reference.TenantId,
        OwnerUserId = reference.OwnerUserId,
        ScopeKey = reference.ScopeKey,
        ReferenceKey = reference.ReferenceKey,
        CreatedAt = reference.CreatedAt,
        ReleasedAt = reference.ReleasedAt,
        Revision = 1
    };

    private static ManagedFileRecord? Map(ManagedFileOwnershipEntity? item) => item is null ? null : new(
        item.Id, item.RootId, item.CanonicalPath, item.ContentSha256, item.Length, item.PlacementMethod,
        item.TenantId, item.OwnerUserId, item.LibraryScopeId, item.SourceJobId, item.ScopeKey,
        item.ReferenceCount, item.IsManaged, item.CreatedAt)
    {
        TargetRootPath = item.TargetRootPath,
        FileSystemDeviceId = item.FileSystemDeviceId,
        FileSystemFileId = item.FileSystemFileId,
        FileSystemLinkCount = item.FileSystemLinkCount
    };

    private static void ValidateReference(ManagedFileRecord record, ManagedFileReference reference)
    {
        if (reference.ManagedFileId != record.Id || reference.TenantId != record.TenantId ||
            reference.OwnerUserId != record.OwnerUserId ||
            !StringComparer.Ordinal.Equals(reference.ScopeKey, record.ScopeKey))
            throw new UnauthorizedAccessException("The managed-file reference is outside its ownership scope.");
        if (string.IsNullOrWhiteSpace(reference.ReferenceKey) || reference.ReferenceKey.Length > 1000)
            throw new InvalidOperationException("Managed-file references require a valid stable key.");
    }
}
