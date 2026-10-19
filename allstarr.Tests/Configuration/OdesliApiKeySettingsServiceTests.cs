using System.Text.Json;
using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed class OdesliApiKeySettingsServiceTests
{
    [Fact]
    public async Task Key_is_encrypted_rotated_loaded_at_startup_and_explicitly_removed()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (service, settings, actor) = await CreateAsync(database);
        await service.ApplyAsync(actor, "private-song-link", null, [], default);
        await service.ApplyAsync(actor, "replacement-song-link", 1, [], default);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var reference = Assert.Single(await db.SecretReferences.ToListAsync());
            Assert.Null(reference.UserId);
            Assert.Equal("odesli-api-key", reference.Purpose);
            Assert.Equal(2, reference.ActiveVersion);
            Assert.Equal(2, await db.SecretVersions.CountAsync());
            var saved = JsonSerializer.Serialize(await db.RuntimeSettings.ToListAsync());
            Assert.DoesNotContain("song-link", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("song-link", JsonSerializer.Serialize(await db.AuditEvents.ToListAsync()), StringComparison.Ordinal);
        }
        var restarted = new ConfigurationManager { ["Storage:DataDirectory"] = database.StorageOptions.DataDirectory };
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("replacement-song-link", restarted["Odesli:ApiKey"]);

        await service.ApplyAsync(actor, "", 2, [], default);
        restarted["Odesli:ApiKey"] = "deployment-song-link";
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("", restarted["Odesli:ApiKey"]);
        await using var cleared = new AllstarrDbContext(database.Options);
        Assert.NotNull((await cleared.SecretReferences.SingleAsync()).RevokedAt);
        Assert.Equal("", (await settings.GetAsync(OdesliApiKeySettingsService.ReferenceKey)).NormalizedValue);
    }

    [Fact]
    public async Task Startup_does_not_open_an_unrelated_secret_or_fall_back_to_a_deployment_key()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (service, _, actor) = await CreateAsync(database);
        await service.ApplyAsync(actor, "private-song-link", null, [], default);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.SecretReferences.SingleAsync()).Purpose = "unrelated-service";
            await db.SaveChangesAsync();
        }
        var restarted = new ConfigurationManager
        {
            ["Storage:DataDirectory"] = database.StorageOptions.DataDirectory,
            ["Odesli:ApiKey"] = "deployment-song-link"
        };
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("", restarted["Odesli:ApiKey"]);
    }

    [Fact]
    public async Task Disabled_administrator_cannot_store_a_key()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (service, _, actor) = await CreateAsync(database);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.Users.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ApplyAsync(actor, "private-song-link", null, [], default));
        await using var unchanged = new AllstarrDbContext(database.Options);
        Assert.Empty(await unchanged.SecretReferences.ToListAsync());
        Assert.Empty(await unchanged.RuntimeSettings.ToListAsync());
    }

    [Fact]
    public async Task Invalid_or_stale_batch_cannot_rotate_or_persist_a_key()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (service, _, actor) = await CreateAsync(database);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(actor, "private-song-link", null,
            [new("Odesli:RateLimitMs", "-1")], default));
        await using (var db = new AllstarrDbContext(database.Options))
        {
            Assert.Empty(await db.SecretReferences.ToListAsync());
            Assert.Empty(await db.RuntimeSettings.ToListAsync());
        }
        await service.ApplyAsync(actor, "private-song-link", null, [], default);
        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() =>
            service.ApplyAsync(actor, "replacement-song-link", null, [], default));
        await using var unchanged = new AllstarrDbContext(database.Options);
        Assert.Equal(1, (await unchanged.SecretReferences.SingleAsync()).ActiveVersion);
        Assert.Single(await unchanged.SecretVersions.ToListAsync());
    }

    private static async Task<(OdesliApiKeySettingsService Service, DurableRuntimeSettingsService Settings, AdminAuthSession Actor)> CreateAsync(SqliteTestDatabase database)
    {
        var factory = new Factory(database.Options);
        var configuration = new ConfigurationManager { ["Storage:DataDirectory"] = database.StorageOptions.DataDirectory };
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        var clock = new SystemPlatformClock();
        var settings = new DurableRuntimeSettingsService(factory, configuration, clock, new RuntimeSettingsChangeSignal());
        var actor = new AdminAuthSession
        {
            SessionId = "session-fixture",
            UserId = "administrator",
            UserName = "Administrator",
            IsAdministrator = true,
            AllstarrUserId = Guid.CreateVersion7(),
            BackendType = "Jellyfin",
            BackendInstanceId = "primary",
            JellyfinAccessToken = "",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        await using var db = new AllstarrDbContext(database.Options);
        db.Users.Add(new UserRecord
        {
            Id = actor.AllstarrUserId.Value,
            DisplayName = "Administrator",
            Enabled = true,
            IsAdmin = true,
            BackendType = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = actor.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (new(factory, settings, new(factory, ring, options, clock), clock), settings, actor);
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
}
