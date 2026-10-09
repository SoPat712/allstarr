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
        await using var auth = await AdminAuthSessionTestSupport.CreateLinkedAsync(
            store, dataProtection);
        var authentication = allstarr.Services.Subsonic.SubsonicSessionAuthentication.Create("alice", "session-password");
        var created = await auth.CreateSessionAsync(
            userId: "alice",
            userName: "alice",
            isAdministrator: true,
            jellyfinAccessToken: string.Empty,
            jellyfinServerId: null,
            backendType: "Subsonic",
            subsonicReadAuthentication: authentication);

        var restored = await auth.CreateService()
            .GetValidSessionAsync(created.SessionId);

        Assert.NotNull(restored);
        Assert.Equal("Subsonic", restored.BackendType);
        Assert.Equal(string.Empty, restored.JellyfinAccessToken);
        Assert.NotNull(restored.AllstarrUserId);
        Assert.Equal(authentication, restored.SubsonicReadAuthentication);
        Assert.DoesNotContain("p", restored.SubsonicReadAuthentication!.Keys);
        Assert.DoesNotContain("session-password", System.Text.Json.JsonSerializer.Serialize(restored), StringComparison.Ordinal);
        Assert.DoesNotContain(authentication["t"], store.Records[created.SessionId].ProtectedPayload, StringComparison.Ordinal);
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
    public async Task SqliteSession_SurvivesServiceRestart()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();

        var factory = new Factory(database.Options);
        var dataProtection = new EphemeralDataProtectionProvider();
        var identityOptions = new allstarr.Core.Identity.IdentityOptions { BackendInstanceId = "backend" };
        var userId = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = userId,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "id",
                DisplayName = "alice",
                IsAdmin = true,
                Enabled = true
            });
            await db.SaveChangesAsync();
        }
        var created = await new AdminAuthSessionService(
                new EfAdminAuthSessionStore(factory),
                dataProtection,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminAuthSessionService>.Instance,
                contextFactory: factory,
                identityOptions: identityOptions)
            .CreateSessionAsync("id", "alice", true, "secret-token", "server",
                allstarrUserId: userId);

        var restored = await new AdminAuthSessionService(
                new EfAdminAuthSessionStore(factory),
                dataProtection,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminAuthSessionService>.Instance,
                contextFactory: factory,
                identityOptions: identityOptions)
            .GetValidSessionAsync(created.SessionId);

        Assert.NotNull(restored);
        Assert.Equal("alice", restored.UserName);
        await using var verification = new AllstarrDbContext(database.Options);
        var record = await verification.AdminAuthSessions.SingleAsync();
        Assert.DoesNotContain("alice", record.ProtectedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", record.ProtectedPayload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("principal")]
    [InlineData("backend")]
    [InlineData("instance")]
    [InlineData("role")]
    [InlineData("unlinked")]
    public async Task NativeSession_RechecksActiveUserAndExactBackendIdentity(string change)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var user = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = user,
                Enabled = true,
                DisplayName = "Listener",
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "listener"
            });
            await db.SaveChangesAsync();
        }
        var store = new MemoryAdminAuthSessionStore();
        var service = new AdminAuthSessionService(store, new EphemeralDataProtectionProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminAuthSessionService>.Instance,
            contextFactory: factory, identityOptions: new allstarr.Core.Identity.IdentityOptions { BackendInstanceId = "backend" });
        var session = await service.CreateSessionAsync("listener", "Listener", false, "fixture", null,
            allstarrUserId: user);
        Assert.NotNull(await service.GetValidSessionAsync(session.SessionId));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var identity = await db.Users.SingleAsync();
            if (change == "disabled") identity.Enabled = false;
            if (change == "principal") identity.BackendPrincipalId = "other";
            if (change == "backend") identity.BackendType = "subsonic";
            if (change == "instance") identity.BackendInstanceId = "other";
            if (change == "role") identity.IsAdmin = true;
            if (change == "unlinked") db.Users.Remove(identity);
            await db.SaveChangesAsync();
        }
        Assert.Null(await service.GetValidSessionAsync(session.SessionId));
        Assert.Empty(store.Records);
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
