using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class PersonalProviderUseModesMigrationTests
{
    private const string Before = "20261016012900_VerifyCopiedManagedFiles";

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradePreservesSettingsAndPersonalKeyRestrictionsAndGuardsLossyRollback()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        var userId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new()
        {
            Id = userId,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        var display = Row(userId, "Playback:ShowExternalLabel", RuntimeSettingValueType.Boolean, "false", now);
        var lyrics = Row(userId, "Providers:LyricsOrder", RuntimeSettingValueType.StringList, "[\"lrclib\",\"spotify\"]", now);
        var household = Row(null, ProviderUsePolicy.SettingKey, RuntimeSettingValueType.String, "\"{}\"", now);
        db.RuntimeSettings.AddRange(display, lyrics, household);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();

        foreach (var expected in new[] { display, lyrics, household })
        {
            var actual = await db.RuntimeSettings.AsNoTracking().SingleAsync(item => item.Id == expected.Id);
            Assert.Equal(expected.ValueJson, actual.ValueJson);
            Assert.Equal(expected.OwnerUserId, actual.OwnerUserId);
            Assert.Equal(7, actual.Revision);
            Assert.Equal(now, actual.CreatedAt);
            Assert.Equal(now, actual.UpdatedAt);
        }
        var sourceMode = Row(userId, ProviderUsePolicy.SettingKey, RuntimeSettingValueType.String,
            "\"{\\\"artistgrid\\\":\\\"off\\\"}\"", now);
        db.RuntimeSettings.Add(sourceMode);
        await db.SaveChangesAsync();
        var forbidden = Row(userId, "Providers:StreamingOrder", RuntimeSettingValueType.StringList, "[\"artistgrid\"]", now);
        db.RuntimeSettings.Add(forbidden);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(forbidden).State = EntityState.Detached;

        var blocked = await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(Before));
        Assert.Contains("CK_source_preferences_requires_backup_restore", blocked.Message);
        Assert.Equal(sourceMode.ValueJson, (await db.RuntimeSettings.AsNoTracking()
            .SingleAsync(item => item.Id == sourceMode.Id)).ValueJson);
        db.RuntimeSettings.Remove(sourceMode);
        await db.SaveChangesAsync();
        await migrator.MigrateAsync(Before);
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.RuntimeSettings.CountAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(3, await db.RuntimeSettings.CountAsync());
    }

    private static RuntimeSettingRecord Row(Guid? owner, string key, RuntimeSettingValueType type, string json,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
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
