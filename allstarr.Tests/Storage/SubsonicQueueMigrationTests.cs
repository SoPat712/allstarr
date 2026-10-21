using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class SubsonicQueueMigrationTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradeAddsQueuesWithoutChangingExistingUsersAndPreservesPayloadOnReopen()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync("20261021090000_AudioQualityChecks");
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new UserRecord
        {
            Id = id,
            BackendType = "subsonic",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        const string savedPayload = """{"ids":["native","external","external"],"currentIndex":2,"position":12345}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO subsonic_play_queues (BackendInstanceId, BackendPrincipalId, PayloadJson)
            VALUES ('fixture', 'listener', {savedPayload})
            """);
        await using var reopened = new AllstarrDbContext(database.Options);
        Assert.Equal("Listener", (await reopened.Users.AsNoTracking().SingleAsync(item => item.Id == id)).DisplayName);
        var payload = await reopened.Database.SqlQueryRaw<string>("""
            SELECT PayloadJson AS Value FROM subsonic_play_queues
            WHERE BackendInstanceId = 'fixture' AND BackendPrincipalId = 'listener'
            """).SingleAsync();
        Assert.Equal(savedPayload, payload);
        await reopened.GetService<IMigrator>().MigrateAsync("20261021090000_AudioQualityChecks");
        Assert.Equal(1, await reopened.Users.CountAsync());
        await reopened.Database.MigrateAsync();
        Assert.Equal(1, await reopened.Users.CountAsync());
    }
}
