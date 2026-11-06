using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class PromotionRequestMigrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PromotionRequestsMigration_PopulatedDowngradeRefusesToDiscardRequestsOrTrust(bool trusted)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = Guid.CreateVersion7();
        db.Users.Add(new() { Id = owner, BackendPrincipalId = "request-fixture", DisplayName = "Listener", IsTrustedListener = trusted });
        if (!trusted)
            db.PromotionRequests.Add(new()
            {
                Id = Guid.CreateVersion7(),
                RequesterUserId = owner,
                RecordingId = Guid.NewGuid(),
                IdentityKey = "fixture",
                Title = "Album",
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261105120000_PromotionProfiles"));
        Assert.Contains("CK_promotion_requests_require_backup_restore", error.Message);
        Assert.Equal(trusted, (await db.Users.AsNoTracking().SingleAsync()).IsTrustedListener);
        Assert.Equal(trusted ? 0 : 1, await db.PromotionRequests.CountAsync());
    }

    [Fact]
    public async Task PromotionRequestsMigration_UpgradesCopyOfExistingDatabaseWithoutChangingExistingData()
    {
        await using var original = await SqliteTestDatabase.CreateAsync(false);
        var owner = Guid.CreateVersion7();
        const string profile = "{\"Values\":{\"DefaultUnit\":\"song\"},\"AddWithoutApproval\":\"admins\"}";
        await using (var db = new AllstarrDbContext(original.Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261105120000_PromotionProfiles");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users (Id, BackendType, BackendInstanceId, BackendPrincipalId, DisplayName,
                    IsAdmin, IsSynthetic, Enabled, CreatedAt, UpdatedAt, LastSeenAt)
                VALUES ({owner}, 'jellyfin', 'fixture', 'listener', 'Listener', 0, 0, 1, 0, 0, 0)
                """);
            db.PromotionProfiles.Add(new() { Key = "household", PayloadJson = profile, Revision = 1 });
            await db.SaveChangesAsync();
        }
        await using var copy = await SqliteTestDatabase.CreateAsync(false);
        File.Copy(original.DatabasePath, copy.DatabasePath);
        await using var upgraded = new AllstarrDbContext(copy.Options);
        await upgraded.Database.MigrateAsync();
        var listener = await upgraded.Users.SingleAsync();
        Assert.Equal(owner, listener.Id);
        Assert.Equal("Listener", listener.DisplayName);
        Assert.False(listener.IsTrustedListener);
        Assert.Equal(profile, (await upgraded.PromotionProfiles.SingleAsync()).PayloadJson);
        Assert.Empty(await upgraded.PromotionRequests.ToArrayAsync());
        Assert.False(upgraded.Database.HasPendingModelChanges());
        Assert.Equal("ok", await upgraded.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
        await using var unchanged = new AllstarrDbContext(original.Options);
        Assert.DoesNotContain((await unchanged.Database.GetAppliedMigrationsAsync()), name => name.EndsWith("_AddPromotionRequests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromotionRequestsMigration_HasOnlyAdditiveUpgradeOperations()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Single(item => item.Key.EndsWith("_AddPromotionRequests", StringComparison.Ordinal)).Value,
            db.Database.ProviderName!);
        Assert.All(migration.UpOperations, operation => Assert.True(operation is
            Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation or
            Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation or
            Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation));
        Assert.NotNull(migration.TargetModel.FindEntityType(typeof(PromotionRequestRecord)));
    }
}
