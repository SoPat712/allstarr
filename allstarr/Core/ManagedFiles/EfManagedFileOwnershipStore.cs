using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed class EfManagedFileOwnershipStore(AllstarrDbContext dbContext) : IManagedFileOwnershipStore, IManagedFileRemovalStore
{
    private DbSet<ManagedFileOwnershipEntity> Files => dbContext.Set<ManagedFileOwnershipEntity>();

    public async Task<ManagedFileRecord?> FindByPathAsync(string canonicalPath, CancellationToken cancellationToken) =>
        Map(await Files.AsNoTracking().SingleOrDefaultAsync(item => item.CanonicalPath == canonicalPath && item.RemovedAt == null, cancellationToken));

    public async Task<ManagedFileRecord?> FindCompatibleAsync(Guid rootId, string contentSha256, string scopeKey, CancellationToken cancellationToken) =>
        Map(await Files.AsNoTracking().OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(item =>
            item.RootId == rootId && item.ContentSha256 == contentSha256 && item.ScopeKey == scopeKey && item.RemovedAt == null, cancellationToken));

    public async Task<ManagedFileRecord> AddAsync(ManagedFileRecord record, CancellationToken cancellationToken)
    {
        var entity = ToEntity(record);
        Files.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return Map(entity)!;
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            var winner = await Files.AsNoTracking().SingleOrDefaultAsync(item =>
                item.CanonicalPath == record.CanonicalPath && item.RemovedAt == null, cancellationToken);
            if (winner is null || winner.RootId != record.RootId || winner.ContentSha256 != record.ContentSha256 || winner.ScopeKey != record.ScopeKey)
                throw;
            return await AddReferenceAsync(winner.Id, cancellationToken);
        }
    }

    public async Task<ManagedFileRecord> AddReferenceAsync(Guid id, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entity = await Files.SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken)
                ?? throw new KeyNotFoundException("Managed file not found.");
            entity.ReferenceCount++;
            entity.Revision++;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return Map(entity)!;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                dbContext.Entry(entity).State = EntityState.Detached;
            }
        }
        throw new DbUpdateConcurrencyException("The managed-file reference changed concurrently.");
    }

    public async Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Map(await Files.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken));

    public async Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await Files.SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Managed file not found.");
        if (entity.ReferenceCount != 1 || !entity.IsManaged)
            throw new InvalidOperationException("Managed-file ownership changed before removal completed.");
        entity.ReferenceCount = 0;
        entity.RemovedAt = DateTimeOffset.UtcNow;
        entity.Revision++;
        await dbContext.SaveChangesAsync(cancellationToken);
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

    private static ManagedFileRecord? Map(ManagedFileOwnershipEntity? item) => item is null ? null : new(
        item.Id, item.RootId, item.CanonicalPath, item.ContentSha256, item.Length, item.PlacementMethod,
        item.TenantId, item.OwnerUserId, item.LibraryScopeId, item.SourceJobId, item.ScopeKey,
        item.ReferenceCount, item.IsManaged, item.CreatedAt)
    { TargetRootPath = item.TargetRootPath };
}
