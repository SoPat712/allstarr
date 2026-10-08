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
        Assert.Single(await reader.Database.GetAppliedMigrationsAsync());
        writer.Tenants.Add(new TenantRecord
        {
            Id = Guid.CreateVersion7(),
            Slug = "isolated",
            Name = "Isolated",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await writer.SaveChangesAsync();
        Assert.Single(await writer.Tenants.ToListAsync());
        Assert.Empty(await reader.Tenants.ToListAsync());
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
        Assert.Single(await intact.Database.GetAppliedMigrationsAsync());
    }
}
