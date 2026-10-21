using allstarr.Core.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class AudioQualityMigrationTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task QualityUpgradeIsNullableAndPreservesRowsAndGuards()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new allstarr.Core.Storage.AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync("20261020173403_PersonalProviderUseModes");
        var guards = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name").ToArrayAsync();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO downloaded_song_mappings (Id,ScopeKey,AudioQuality,ProviderId,ExternalId,LocalPath,Title,Artist,Album,DownloadedAt,Revision)
            VALUES ({id},'fixture',0,'fixture','song','/fixture/song.flac','Title','Artist','Album',1,7)
            """);
        await db.Database.MigrateAsync();
        Assert.Equal(guards, await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name").ToArrayAsync());
        Assert.Equal(7, (await db.DownloadedSongMappings.AsNoTracking().SingleAsync()).Revision);
        Assert.True(db.Model.FindEntityType(typeof(ProviderDownloadArtifactEntity))!
            .FindProperty("QualityCheckJson")!.IsNullable);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }
}
