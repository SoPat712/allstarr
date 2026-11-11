using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed class IdentityContentionTests
{
    [Fact]
    public async Task FirstIdentitySurfacesExhaustedWriteTimeoutWithoutRetrying()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var conflict = new WriteConflict();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(conflict).Options;
        await Assert.ThrowsAsync<DbUpdateException>(() => Resolver(database, options)
            .ResolveAsync(new("jellyfin", "new-listener")).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, conflict.Writes);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.Users.ToArrayAsync());
    }

    [Fact]
    public async Task UnavailableIdentityReadSurfacesExhaustedTimeoutWithoutRetrying()
    {
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>();
        factory.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Microsoft.Data.Sqlite.SqliteException("Read timeout fixture.", 5));
        var state = new DurableStorageState(new StorageOptions());
        state.Set(DurableStorageReadiness.Ready);
        var resolver = new BackendIdentityResolver(factory.Object, state, new IdentityOptions(), new SystemPlatformClock());
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => resolver
            .ResolveAsync(new("jellyfin", "listener")).WaitAsync(TimeSpan.FromSeconds(5)));
        factory.Verify(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeferredIdentityRefreshPreservesVerifiedRoleAndOutlivesRequestCancellation()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var original = (await Resolver(database, database.Options).ResolveAsync(new("jellyfin", "listener", "Listener")))!;
        var gate = new WriteGate();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(gate).Options;
        var resolver = Resolver(database, options);
        using var request = new CancellationTokenSource();
        var resolve = resolver.ResolveAsync(new("jellyfin", "listener", "Renamed", true), request.Token);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var principal = (await resolve.WaitAsync(TimeSpan.FromSeconds(5)))!;
            Assert.Equal(original.UserId, principal.UserId);
            Assert.Equal("Renamed", principal.DisplayName);
            Assert.True(principal.IsAdministrator);
            Assert.False(gate.Saved.Task.IsCompleted);
            Assert.Equal(1, gate.Writes);
            request.Cancel();
        }
        finally
        {
            gate.Release.TrySetResult();
            await gate.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await using var db = new AllstarrDbContext(database.Options);
        var saved = await db.Users.SingleAsync();
        Assert.Equal(original.UserId, saved.Id);
        Assert.Equal("Renamed", saved.DisplayName);
        Assert.True(saved.IsAdmin);
        Assert.Equal(1, gate.Writes);
    }

    [Fact]
    public async Task PermanentBestEffortWriteFailureIsLoggedAndDoesNotRejectExistingIdentity()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var original = (await Resolver(database, database.Options).ResolveAsync(new("jellyfin", "listener", "Listener")))!;
        var failure = new WriteFailure();
        var logger = new Mock<ILogger<BackendIdentityResolver>>();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(failure).Options;
        var principal = await Resolver(database, options, logger.Object).ResolveAsync(new("jellyfin", "listener", "Renamed", true));
        Assert.Equal(original.UserId, principal!.UserId);
        Assert.Equal("Renamed", principal.DisplayName);
        Assert.True(principal.IsAdministrator);
        Assert.Equal(1, failure.Writes);
        logger.Verify(item => item.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal("Listener", (await db.Users.SingleAsync()).DisplayName);
    }

    private static BackendIdentityResolver Resolver(SqliteTestDatabase database, DbContextOptions<AllstarrDbContext> options,
        ILogger<BackendIdentityResolver>? logger = null)
    {
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>();
        factory.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AllstarrDbContext(options));
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        return new(factory.Object, state, new IdentityOptions(), new SystemPlatformClock(), logger);
    }

    private sealed class WriteGate : SaveChangesInterceptor
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writes);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Saved.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class WriteFailure : SaveChangesInterceptor
    {
        public int Writes;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Writes++;
            throw new DbUpdateException("Storage write fixture failed.");
        }
    }

    private sealed class WriteConflict : SaveChangesInterceptor
    {
        public int Writes;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Writes++;
            throw new DbUpdateException("Write timeout fixture.", new Microsoft.Data.Sqlite.SqliteException("Busy fixture.", 5));
        }
    }
}
