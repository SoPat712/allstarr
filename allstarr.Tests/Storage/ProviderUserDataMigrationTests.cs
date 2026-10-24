using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class ProviderUserDataMigrationTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UserData_UpgradePreservesUsersAndPersistsRatingAcrossReopen()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync("20261021120000_SubsonicPlayQueues");
        var user = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id, BackendType, BackendInstanceId, BackendPrincipalId, DisplayName, IsAdmin, Enabled, CreatedAt, UpdatedAt, LastSeenAt)
            VALUES ({user}, 'subsonic', 'fixture', 'listener', 'Listener', 0, 1, {now.UtcTicks}, {now.UtcTicks}, 0);
            """);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO provider_user_data (OwnerUserId, Protocol, BackendInstanceId, ItemId, Rating)
            VALUES ({user}, 'subsonic', 'fixture', 'ext-deezer-song-test', 4)
            """);
        await using var reopened = new AllstarrDbContext(database.Options);
        Assert.Equal(4, await reopened.Database.SqlQueryRaw<int>("SELECT Rating AS Value FROM provider_user_data").SingleAsync());
        Assert.Equal("Listener", (await reopened.Users.SingleAsync(item => item.Id == user)).DisplayName);
        Assert.False(reopened.Database.HasPendingModelChanges());
    }
}
