using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class AdminAuthSessionServiceTests
{
    [Fact]
    public async Task SubsonicIdentitySession_PersistsWithoutPersistingBackendPassword()
    {
        var store = new MemoryAdminAuthSessionStore();
        var dataProtection = new EphemeralDataProtectionProvider();
        var first = AdminAuthSessionTestSupport.Create(store, dataProtection);
        var tenantId = Guid.CreateVersion7();
        var allstarrUserId = Guid.CreateVersion7();
        var created = await first.CreateSessionAsync(
            userId: "alice",
            userName: "alice",
            isAdministrator: true,
            jellyfinAccessToken: string.Empty,
            jellyfinServerId: null,
            backendType: "Subsonic",
            tenantId: tenantId,
            allstarrUserId: allstarrUserId);

        var restored = await AdminAuthSessionTestSupport.Create(store, dataProtection)
            .GetValidSessionAsync(created.SessionId);

        Assert.NotNull(restored);
        Assert.Equal("Subsonic", restored.BackendType);
        Assert.Equal(string.Empty, restored.JellyfinAccessToken);
        Assert.Equal(tenantId, restored.TenantId);
        Assert.Equal(allstarrUserId, restored.AllstarrUserId);
        Assert.DoesNotContain("alice", store.Records[created.SessionId].ProtectedPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptSessionStore_LogsOnlyExceptionType()
    {
        var store = new MemoryAdminAuthSessionStore();
        store.Records["bad"] = new()
        {
            Id = "bad",
            ProtectedPayload = "not-a-valid-protected-payload-private-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastSeenAt = DateTimeOffset.UtcNow
        };
        var entries = new List<(string Message, Exception? Exception)>();
        var service = AdminAuthSessionTestSupport.Create(
            store,
            logger: new CollectingLogger<AdminAuthSessionService>(entries));

        Assert.Null(await service.GetValidSessionAsync("bad"));
        Assert.Empty(store.Records);
        var entry = Assert.Single(entries);
        Assert.Null(entry.Exception);
        Assert.Contains("CryptographicException", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredSession_IsRejectedAndDeleted()
    {
        var store = new MemoryAdminAuthSessionStore();
        var service = AdminAuthSessionTestSupport.Create(store);
        var session = await service.CreateSessionAsync("id", "name", true, "token", null);
        store.Records[session.SessionId].ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        Assert.Null(await service.GetValidSessionAsync(session.SessionId));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task PostgreSqlSession_SurvivesServiceRestart()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var context = new AllstarrDbContext(database.Options))
        {
            await context.Database.MigrateAsync();
        }

        var factory = new Factory(database.Options);
        var dataProtection = new EphemeralDataProtectionProvider();
        var created = await new AdminAuthSessionService(
                new EfAdminAuthSessionStore(factory),
                dataProtection,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminAuthSessionService>.Instance)
            .CreateSessionAsync("id", "alice", true, "secret-token", "server");

        var restored = await new AdminAuthSessionService(
                new EfAdminAuthSessionStore(factory),
                dataProtection,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminAuthSessionService>.Instance)
            .GetValidSessionAsync(created.SessionId);

        Assert.NotNull(restored);
        Assert.Equal("alice", restored.UserName);
        await using var verification = new AllstarrDbContext(database.Options);
        var record = await verification.AdminAuthSessions.SingleAsync();
        Assert.DoesNotContain("alice", record.ProtectedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", record.ProtectedPayload, StringComparison.Ordinal);
    }

    private sealed class CollectingLogger<T>(List<(string Message, Exception? Exception)> entries)
        : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Add((formatter(state, exception), exception));
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
