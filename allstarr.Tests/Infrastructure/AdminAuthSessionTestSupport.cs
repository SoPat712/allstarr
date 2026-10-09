using allstarr.Core.Storage;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

internal static class AdminAuthSessionTestSupport
{
    public static AdminAuthSessionService Create(
        MemoryAdminAuthSessionStore? store = null,
        IDataProtectionProvider? dataProtection = null,
        ILogger<AdminAuthSessionService>? logger = null) =>
        new(
            store ?? new MemoryAdminAuthSessionStore(),
            dataProtection ?? new EphemeralDataProtectionProvider(),
            logger ?? NullLogger<AdminAuthSessionService>.Instance);

    public static async Task<AdminAuthSessionFixture> CreateLinkedAsync(
        MemoryAdminAuthSessionStore? store = null,
        IDataProtectionProvider? dataProtection = null,
        ILogger<AdminAuthSessionService>? logger = null,
        string backendInstanceId = "primary")
    {
        var database = await SqliteTestDatabase.CreateAsync();
        return new AdminAuthSessionFixture(
            database,
            store ?? new MemoryAdminAuthSessionStore(),
            dataProtection ?? new EphemeralDataProtectionProvider(),
            logger ?? NullLogger<AdminAuthSessionService>.Instance,
            backendInstanceId);
    }
}

internal sealed class AdminAuthSessionFixture : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger<AdminAuthSessionService> _logger;

    public AdminAuthSessionFixture(
        SqliteTestDatabase database,
        MemoryAdminAuthSessionStore store,
        IDataProtectionProvider dataProtection,
        ILogger<AdminAuthSessionService> logger,
        string backendInstanceId)
    {
        _database = database;
        Store = store;
        _dataProtection = dataProtection;
        _logger = logger;
        Factory = new AdminAuthTestDbContextFactory(database.Options);
        IdentityOptions = new IdentityOptions { BackendInstanceId = backendInstanceId };
        var storageState = new DurableStorageState(database.StorageOptions);
        storageState.Set(DurableStorageReadiness.Ready);
        Identities = new BackendIdentityResolver(
            Factory, storageState, IdentityOptions, new SystemPlatformClock());
        Service = CreateService();
    }

    public MemoryAdminAuthSessionStore Store { get; }
    public IDbContextFactory<AllstarrDbContext> Factory { get; }
    public IdentityOptions IdentityOptions { get; }
    public BackendIdentityResolver Identities { get; }
    public AdminAuthSessionService Service { get; }

    public AdminAuthSessionService CreateService(IAdminAuthSessionStore? store = null) => new(
        store ?? Store,
        _dataProtection,
        _logger,
        contextFactory: Factory,
        identityOptions: IdentityOptions);

    public async Task<AdminAuthSession> CreateSessionAsync(
        string userId,
        string userName,
        bool isAdministrator,
        string jellyfinAccessToken = "token",
        string? jellyfinServerId = "server",
        string backendType = "Jellyfin",
        Dictionary<string, string>? subsonicReadAuthentication = null)
    {
        var principal = await Identities.ResolveAsync(new BackendIdentityDescriptor(
            backendType,
            userId,
            userName,
            isAdministrator,
            IdentityOptions.BackendInstanceId));
        return await Service.CreateSessionAsync(
            userId,
            userName,
            isAdministrator,
            jellyfinAccessToken,
            jellyfinServerId,
            backendType: backendType,
            allstarrUserId: principal!.UserId,
            subsonicReadAuthentication: subsonicReadAuthentication);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}

internal sealed class AdminAuthTestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
    : IDbContextFactory<AllstarrDbContext>
{
    public AllstarrDbContext CreateDbContext() => new(options);

    public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AllstarrDbContext(options));
}

internal sealed class MemoryAdminAuthSessionStore : IAdminAuthSessionStore
{
    public Dictionary<string, AdminAuthSessionRecord> Records { get; } = new(StringComparer.Ordinal);

    public Task<AdminAuthSessionRecord?> FindAsync(string id, CancellationToken cancellationToken)
    {
        Records.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task AddAsync(AdminAuthSessionRecord record, CancellationToken cancellationToken)
    {
        Records.Add(record.Id, record);
        return Task.CompletedTask;
    }

    public Task TouchAsync(string id, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        if (Records.TryGetValue(id, out var record)) record.LastSeenAt = lastSeenAt;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        Records.Remove(id);
        return Task.CompletedTask;
    }

    public Task RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var id in Records.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
        {
            Records.Remove(id);
        }
        return Task.CompletedTask;
    }
}
