using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.DataProtection;
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
