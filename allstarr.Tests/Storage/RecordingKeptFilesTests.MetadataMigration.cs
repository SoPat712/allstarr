using allstarr.Core.ManagedFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeptMetadataUpgradePreservesLegacyBytesReferencesAndOwnershipGuards()
    {
        var published = await Service().PublishAsync(Request() with
        { Metadata = new("Kept title", ["Artist"], "Album", 123_457) });
        await using var db = new allstarr.Core.Storage.AllstarrDbContext(database.Options);
        var references = await db.ManagedFileReferences.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.ReferenceKey, item.ManagedFileId, item.ReleasedAt, item.Revision }).ToArrayAsync();
        var triggers = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'trigger' ORDER BY name").ToArrayAsync();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261015083606_RecordFavoriteIntentAtAcceptance");

        Assert.Equal(triggers, await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'trigger' ORDER BY name").ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
        await db.Database.MigrateAsync();
        var legacy = await db.Set<RecordingKeptRouteEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(published.ManagedFileId, legacy.ManagedFileId);
        Assert.Equal(owner, legacy.PublishedByUserId);
        Assert.Equal(jobId, legacy.PublicationJobId);
        Assert.Equal(artifact.Id, legacy.SourceArtifactId);
        Assert.Null(legacy.DisplayMetadataJson);
        Assert.Equal(references, await db.ManagedFileReferences.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.ReferenceKey, item.ManagedFileId, item.ReleasedAt, item.Revision }).ToArrayAsync());
        Assert.Equal(triggers, await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'trigger' ORDER BY name").ToArrayAsync());
        Assert.Equal(published.ManagedFileId, (await Service().FindAsync(Actor(other), recordingId))!.ManagedFileId);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }
}
