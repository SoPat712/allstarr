using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class PersonalLyricsOrderMigrationTests
{
    private const string Before = "20261014174328_BackfillPlaylistRemovalConfirmations";

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradePreservesExistingSettingsAndAllowsOnlyTheAdditionalPersonalKey()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id, BackendType, BackendInstanceId, BackendPrincipalId, DisplayName, IsAdmin, Enabled, CreatedAt, UpdatedAt, LastSeenAt)
            VALUES ({user}, 'jellyfin', 'fixture', 'listener', 'Listener', 0, 1, {now.UtcTicks}, {now.UtcTicks}, 0);
            """);
        var display = Row(user, "Playback:ShowExternalLabel", RuntimeSettingValueType.Boolean, "false", now);
        var household = Row(null, "Providers:LyricsOrder", RuntimeSettingValueType.StringList, "[\"spotify\",\"lrclib\"]", now);
        db.RuntimeSettings.AddRange(display, household);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();
        var beforeRows = await db.RuntimeSettings.AsNoTracking().OrderBy(item => item.Key).ToArrayAsync();
        Assert.Equal(new[] { display.Id, household.Id }, beforeRows.Select(item => item.Id));
        Assert.All(beforeRows, row =>
        {
            Assert.Equal(now, row.CreatedAt);
            Assert.Equal(now, row.UpdatedAt);
            Assert.Equal(7, row.Revision);
        });
        Assert.Equal(household.ValueJson, beforeRows[1].ValueJson);
        Assert.Equal(display.ValueJson, beforeRows[0].ValueJson);
        db.RuntimeSettings.Add(Row(user, "Providers:LyricsOrder", RuntimeSettingValueType.StringList, "[\"lrclib\",\"spotify\"]", now));
        await db.SaveChangesAsync();
        var forbidden = Row(user, "Cache:LyricsDays", RuntimeSettingValueType.Integer, "30", now);
        db.RuntimeSettings.Add(forbidden);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(forbidden).State = EntityState.Detached;
        Assert.Equal(3, await db.RuntimeSettings.CountAsync());

        await db.GetService<IMigrator>().MigrateAsync(Before);
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { display.Id, household.Id },
            await db.RuntimeSettings.OrderBy(item => item.Key).Select(item => item.Id).ToArrayAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(2, await db.RuntimeSettings.CountAsync());
    }

    private static RuntimeSettingRecord Row(Guid? owner, string key, RuntimeSettingValueType type, string json, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = owner,
        Key = key,
        ValueType = type,
        ValueJson = json,
        Source = "fixture",
        CreatedAt = now,
        UpdatedAt = now,
        Revision = 7
    };
}
