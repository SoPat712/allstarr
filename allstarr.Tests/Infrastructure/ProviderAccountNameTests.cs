using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ProviderAccountNameTests
{
    [Fact]
    public async Task StartupRenamesOnlyGeneratedLabelsAndDoesNotTouchCredentialsOrOwnership()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var names = new[] { "My lastfm", "Shared lastfm", "Legacy .env import", "Legacy .env import (current user)",
            "My Last.fm account", "Shared Last.fm account", "My Last.fm connection", "Last.fm", "Road trip",
            "My lastfm favourites", "Shared lastfm family", "Legacy collection" };
        await using (var context = factory.CreateDbContext())
        {
            context.ProviderAccounts.AddRange(names.Select(name => new ProviderAccountRecord
            {
                Id = Guid.CreateVersion7(),
                ProviderId = "lastfm",
                DisplayName = name,
                SettingsJson = "{\"endpoint\":\"fixture\"}",
                Revision = 7,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            }));
            context.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = Guid.CreateVersion7(),
                ProviderId = "missing-extension",
                DisplayName = "My missing-extension",
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync();
        }
        var state = new DurableStorageState(new StorageOptions());
        state.Set(DurableStorageReadiness.Ready);
        var normalizer = new ProviderAccountNameNormalizer(factory, new ProviderRegistry([]), state,
            NullLogger<ProviderAccountNameNormalizer>.Instance);
        await normalizer.StartAsync(CancellationToken.None);
        await using var verify = factory.CreateDbContext();
        var accounts = await verify.ProviderAccounts.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(8, accounts.Count(item => item.DisplayName == "Last.fm"));
        Assert.All(names.Skip(8), name => Assert.Contains(accounts, account => account.DisplayName == name));
        Assert.Contains(accounts, item => item.DisplayName == "My missing-extension");
        Assert.All(accounts.Where(item => item.ProviderId == "lastfm"), item =>
        {
            Assert.True(item.Enabled);
            Assert.Null(item.OwnerUserId);
            Assert.Null(item.SecretReferenceId);
            Assert.Equal("{\"endpoint\":\"fixture\"}", item.SettingsJson);
            Assert.Equal(now, item.CreatedAt);
            Assert.Equal(item.DisplayName == "Last.fm" && item.UpdatedAt != now ? 8 : 7, item.Revision);
        });
        var snapshot = JsonSerializer.Serialize(accounts);
        var auditCount = await verify.AuditEvents.CountAsync();
        Assert.True(auditCount > 0);
        await normalizer.StartAsync(CancellationToken.None);
        Assert.Equal(snapshot, JsonSerializer.Serialize(await verify.ProviderAccounts.AsNoTracking().OrderBy(item => item.Id).ToListAsync()));
        Assert.Equal(auditCount, await verify.AuditEvents.CountAsync());
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
