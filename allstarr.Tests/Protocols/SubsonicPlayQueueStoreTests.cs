using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class SubsonicPlayQueueStoreTests
{
    [Fact]
    public async Task QueueOwnershipUsesVerifiedUserAndBackendAcrossStoreRecreation()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var contexts = new QueueContexts(database.Options);
        var store = new SubsonicPlayQueueStore(contexts);
        var context = Context("one", "listener");
        var saved = new SubsonicSavedQueue([], null, 0, "listener", DateTimeOffset.UtcNow, "Tempo");
        await store.SaveAsync(context, saved, default);
        var restarted = new SubsonicPlayQueueStore(contexts);
        Assert.Equal(saved.Changed, (await restarted.ReadAsync(context, default))!.Changed);
        Assert.Null(await restarted.ReadAsync(Context("two", "listener"), default));
        Assert.Null(await restarted.ReadAsync(Context("one", "other"), default));
        await using var db = await contexts.CreateDbContextAsync();
        Assert.Equal(1, await db.SubsonicPlayQueues.CountAsync());
    }

    private static ProtocolExecutionContext Context(string backend, string viewer) => new(
        ProtocolKind.Subsonic, backend, viewer, null, "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private sealed class QueueContexts(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
