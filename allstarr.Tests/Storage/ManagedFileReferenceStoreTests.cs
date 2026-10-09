using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ManagedFileReferenceStoreTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"allstarr-managed-references-{Guid.NewGuid():N}");
    private SqliteTestDatabase database = null!;
    private DbContextOptions<AllstarrDbContext> options = null!;
    private Guid userId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        database = await SqliteTestDatabase.CreateAsync();
        options = database.Options;
        await using var db = new AllstarrDbContext(options);
        userId = Guid.CreateVersion7();
        db.Users.Add(new UserRecord
        {
            Id = userId,
            DisplayName = "Managed reference user",
            Enabled = true,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = userId.ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Store_PersistsIdempotentAcquireAndOneTimeRelease()
    {
        var fileId = Guid.CreateVersion7();
        var record = Record(fileId);
        await using (var db = new AllstarrDbContext(options))
        {
            var store = new EfManagedFileOwnershipStore(db);
            var first = Reference(fileId, "favorite:a");
            Assert.Equal(1, (await store.AddAsync(record, first, default)).ReferenceCount);
            Assert.Equal(1, (await store.AddReferenceAsync(fileId,
                Reference(fileId, "favorite:a"), default)).ReferenceCount);
            Assert.Equal(2, (await store.AddReferenceAsync(fileId,
                Reference(fileId, "playlist:b"), default)).ReferenceCount);
            Assert.Equal(1, (await store.ReleaseReferenceAsync(fileId, "playlist:b", default)).ReferenceCount);
            Assert.Equal(1, (await store.ReleaseReferenceAsync(fileId, "playlist:b", default)).ReferenceCount);
        }

        await using var verify = new AllstarrDbContext(options);
        Assert.Equal(1, (await verify.ManagedFiles.SingleAsync()).ReferenceCount);
        var references = await verify.ManagedFileReferences.OrderBy(item => item.ReferenceKey).ToListAsync();
        Assert.Equal(2, references.Count);
        Assert.Null(references[0].ReleasedAt);
        Assert.NotNull(references[1].ReleasedAt);
    }

    [Fact]
    public async Task Store_RejectsReferenceOutsideManagedOwnershipScope()
    {
        await using var db = new AllstarrDbContext(options);
        var store = new EfManagedFileOwnershipStore(db);
        var record = Record(Guid.CreateVersion7());
        var reference = Reference(record.Id, "favorite:wrong") with { OwnerUserId = Guid.CreateVersion7() };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AddAsync(record, reference, default));
        Assert.Empty(await db.ManagedFiles.ToListAsync());
        Assert.Empty(await db.ManagedFileReferences.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentAcquireOfSameStableReference_RemainsSingleAndCountedOnce()
    {
        var fileId = Guid.CreateVersion7();
        await using (var seed = new AllstarrDbContext(options))
        {
            var store = new EfManagedFileOwnershipStore(seed);
            await store.AddAsync(Record(fileId), Reference(fileId, "seed"), default);
        }

        async Task Acquire()
        {
            await using var db = new AllstarrDbContext(options);
            var store = new EfManagedFileOwnershipStore(db);
            await store.AddReferenceAsync(fileId, Reference(fileId, "concurrent"), default);
        }

        await Task.WhenAll(Acquire(), Acquire());
        await using var verify = new AllstarrDbContext(options);
        Assert.Equal(2, await verify.ManagedFiles.Where(item => item.Id == fileId)
            .Select(item => item.ReferenceCount).SingleAsync());
        Assert.Equal(2, await verify.ManagedFileReferences.CountAsync(item =>
            item.ManagedFileId == fileId && item.ReleasedAt == null));
    }

    private ManagedFileRecord Record(Guid id) => new(
        id, id, Path.Combine(root, $"{id:N}.flac"), new string('a', 64), 10,
        ManagedFilePlacementMethod.Copy, userId, null, ManagedFileScopeKey.Create(userId, id),
        1, true, DateTimeOffset.UtcNow)
    {
        TargetRootPath = root,
        FileSystemDeviceId = "1a",
        FileSystemFileId = id.ToString("N"),
        FileSystemLinkCount = 1
    };

    private ManagedFileReference Reference(Guid fileId, string key) => new(
        Guid.CreateVersion7(), fileId, userId, ManagedFileScopeKey.Create(userId, fileId), key, DateTimeOffset.UtcNow);

    public async Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        if (database is not null) await database.DisposeAsync();
    }
}
