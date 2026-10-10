using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class SqliteTestDatabaseTests
{
    [Fact]
    public async Task TemplateClonesShareTheBaselineButIsolateRecords()
    {
        await using var first = await SqliteTestDatabase.CreateAsync();
        await using var second = await SqliteTestDatabase.CreateAsync();
        Assert.True(first.IsTemplateBacked);
        Assert.True(second.IsTemplateBacked);
        Assert.NotEqual(first.DatabaseName, second.DatabaseName);
        await using var writer = new AllstarrDbContext(first.Options);
        await using var reader = new AllstarrDbContext(second.Options);
        Assert.Equal(await writer.Database.GetAppliedMigrationsAsync(), await reader.Database.GetAppliedMigrationsAsync());
        Assert.Equal(reader.Database.GetMigrations(), await reader.Database.GetAppliedMigrationsAsync());
        writer.Users.Add(new UserRecord
        {
            Id = Guid.CreateVersion7(),
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "isolated",
            DisplayName = "Isolated",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await writer.SaveChangesAsync();
        Assert.Single(await writer.Users.ToListAsync());
        Assert.Empty(await reader.Users.ToListAsync());
    }

    [Fact]
    public async Task DisposeRemovesOnlyItsOwnedDatabaseAndSidecars()
    {
        await using var neighbor = await SqliteTestDatabase.CreateAsync();
        var database = await SqliteTestDatabase.CreateAsync();
        var directory = Path.GetDirectoryName(database.DatabasePath)!;
        await using (var context = new AllstarrDbContext(database.Options))
        {
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await context.Database.ExecuteSqlRawAsync("CREATE TABLE fixture (id INTEGER PRIMARY KEY);");
        }
        await database.DisposeAsync();
        Assert.False(Directory.Exists(directory));
        Assert.True(File.Exists(neighbor.DatabasePath));
        await using var intact = new AllstarrDbContext(neighbor.Options);
        Assert.Equal(intact.Database.GetMigrations(), await intact.Database.GetAppliedMigrationsAsync());
    }
}
