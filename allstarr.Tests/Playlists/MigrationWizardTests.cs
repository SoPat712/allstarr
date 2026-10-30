using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class MigrationWizardTests
{
    [Fact]
    public async Task MigrationSchemaIsAdditiveAndRequestKeyBelongsToOwner()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Contains("20261030120000_CollectionMigration", db.Database.GetAppliedMigrations());
        var model = db.Model.FindEntityType(typeof(CollectionMigrationRunRecord))!;
        Assert.Contains(model.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual(["OwnerUserId", "RequestKey"]));
        Assert.NotNull(db.Model.FindEntityType(typeof(CollectionMigrationCollectionRecord)));
    }
}
