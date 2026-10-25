using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

[Trait("Lane", "ReleaseCritical")]
public sealed class ManagedAudioCacheMigrationTests
{
    [Fact]
    public async Task UpgradeAndDowngradePreserveEveryHistoricMappingValueAndTrigger()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        const string before = "20261023030000_SyntheticHealthChecks";
        await migrator.MigrateAsync(before);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO downloaded_song_mappings
                (Id,ScopeKey,ProviderAccountId,AudioQuality,ProviderId,ExternalId,LocalPath,Title,Artist,Album,DownloadedAt,Revision)
            VALUES ('11111111-1111-1111-1111-111111111111','legacy',NULL,0,'fixture','song','/fixture/cache/song.mp3','Title','Artist','Album',123,7);
            CREATE TRIGGER mapping_title_guard BEFORE UPDATE OF Title ON downloaded_song_mappings
            WHEN NEW.Title = '' BEGIN SELECT RAISE(ABORT, 'title_required'); END;
            """);
        const string historic = """
            SELECT json_object('Id',Id,'ScopeKey',ScopeKey,'ProviderAccountId',ProviderAccountId,'AudioQuality',AudioQuality,
                'ProviderId',ProviderId,'ExternalId',ExternalId,'LocalPath',LocalPath,'Title',Title,'Artist',Artist,'Album',Album,
                'DownloadedAt',DownloadedAt,'Revision',Revision) AS Value FROM downloaded_song_mappings ORDER BY Id
            """;
        const string triggers = "SELECT name || char(10) || sql AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name";
        var values = await db.Database.SqlQueryRaw<string>(historic).ToArrayAsync();
        var originalTriggers = await db.Database.SqlQueryRaw<string>(triggers).ToArrayAsync();
        await migrator.MigrateAsync();
        Assert.Null((await db.DownloadedSongMappings.AsNoTracking().SingleAsync()).CacheIdentityJson);
        Assert.Equal(values, await db.Database.SqlQueryRaw<string>(historic).ToArrayAsync());
        Assert.Equal(originalTriggers, await db.Database.SqlQueryRaw<string>(triggers).ToArrayAsync());
        await migrator.MigrateAsync(before);
        Assert.Equal(values, await db.Database.SqlQueryRaw<string>(historic).ToArrayAsync());
        Assert.Equal(originalTriggers, await db.Database.SqlQueryRaw<string>(triggers).ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('downloaded_song_mappings') WHERE name='CacheIdentityJson'").SingleAsync());
        await migrator.MigrateAsync();
        Assert.Null((await db.DownloadedSongMappings.AsNoTracking().SingleAsync()).CacheIdentityJson);
        Assert.Equal(values, await db.Database.SqlQueryRaw<string>(historic).ToArrayAsync());
    }
}
