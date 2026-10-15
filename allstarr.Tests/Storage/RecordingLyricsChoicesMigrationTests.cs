using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class RecordingLyricsChoicesMigrationTests
{
    private const string Before = "20261015022113_PersonalLyricsOrder";

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradeAddsScopedChoicesAndPreservesOlderRowsThroughRollback()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);
        var now = DateTimeOffset.UtcNow;
        var owner = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        db.Users.Add(new()
        {
            Id = owner,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.CanonicalRecordings.Add(new()
        {
            Id = recording,
            CreatedByUserId = owner,
            PublicId = "ext-spotify-song-fixture",
            Title = "Fixture",
            IsProvisional = true,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 7
        });
        var setting = new allstarr.Core.Settings.RuntimeSettingRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            Key = "Providers:LyricsOrder",
            ValueType = allstarr.Core.Settings.RuntimeSettingValueType.StringList,
            ValueJson = "[\"lrclib\",\"spotify\"]",
            Source = "fixture",
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 4
        };
        var legacyId = Guid.CreateVersion7();
        db.ManualLyricsMappings.Add(new()
        {
            Id = legacyId,
            IdentityHash = new string('a', 64),
            Artist = "Artist",
            Title = "Fixture",
            LyricsId = 42,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.RuntimeSettings.Add(setting);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.RecordingLyricsChoices.AsNoTracking().ToArrayAsync());
        var legacy = await db.ManualLyricsMappings.AsNoTracking().SingleAsync();
        Assert.Equal(legacyId, legacy.Id);
        Assert.Equal(42, legacy.LyricsId);
        RecordingLyricsChoiceRecord Choice(Guid? user, string source) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            CanonicalRecordingId = recording,
            SourceId = source,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var household = Choice(null, "lrclib");
        var personal = Choice(owner, "spotify");
        db.RecordingLyricsChoices.AddRange(household, personal);
        await db.SaveChangesAsync();
        foreach (var duplicate in new[] { Choice(null, "lyricsplus"), Choice(owner, "lyricsplus") })
        {
            db.RecordingLyricsChoices.Add(duplicate);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.Entry(duplicate).State = EntityState.Detached;
        }
        personal.RevokedAt = now;
        await db.SaveChangesAsync();
        db.RecordingLyricsChoices.Add(Choice(owner, "lyricsplus"));
        await db.SaveChangesAsync();
        await db.GetService<IMigrator>().MigrateAsync(Before);
        db.ChangeTracker.Clear();
        var retained = await db.CanonicalRecordings.AsNoTracking().SingleAsync();
        Assert.Equal(recording, retained.Id);
        Assert.Equal(7, retained.Revision);
        Assert.Equal(now, retained.CreatedAt);
        var saved = await db.RuntimeSettings.AsNoTracking().SingleAsync();
        Assert.Equal(setting.Id, saved.Id);
        Assert.Equal(setting.ValueJson, saved.ValueJson);
        Assert.Equal(4, saved.Revision);
        await db.Database.MigrateAsync();
        Assert.Empty(await db.RecordingLyricsChoices.AsNoTracking().ToArrayAsync());
        Assert.Equal(legacyId, (await db.ManualLyricsMappings.AsNoTracking().SingleAsync()).Id);
    }
}
