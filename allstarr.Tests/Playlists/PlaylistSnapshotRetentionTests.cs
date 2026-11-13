using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Playlists;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class PlaylistSnapshotRetentionTests
{
    [Fact]
    public async Task CleanupRemovesSupersededEntriesAndKeepsCurrentPreviousAndSongMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = new AllstarrDbContext(fixture.Database.Options);
        var old = fixture.Snapshots[0];
        var source = await db.ExternalMetadataSnapshots.SingleAsync();
        var rotated = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = fixture.Owner,
            ProviderId = "spotify",
            ResourceKind = "track",
            ExternalIdHash = Hash("rotated"),
            SnapshotVersion = 1,
            PayloadJson = "{}",
            PayloadSha256 = Hash("{}")
        };
        db.ExternalMetadataSnapshots.Add(rotated);
        db.PlaylistSourceEntries.Add(new()
        {
            Id = Guid.CreateVersion7(),
            PlaylistSourceSnapshotId = old.Id,
            ExternalMetadataSnapshotId = rotated.Id,
            SourcePosition = 1,
            SourceEntryIdHash = Hash("rotated")
        });
        db.ManualTrackOverrides.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = fixture.Owner,
            ExternalSnapshotId = source.Id,
            SourceProviderId = source.ProviderId,
            SourceExternalIdHash = source.ExternalIdHash,
            Decision = ManualOverrideDecision.Reject,
            DecisionVersion = 1,
            Revision = 1
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection().AddDbContext<AllstarrDbContext>(options => options.UseSqlite(fixture.Database.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        await new AuditEventRetentionService(provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(),
            TimeProvider.System, NullLogger<AuditEventRetentionService>.Instance).PruneNowAsync();

        Assert.False(await db.PlaylistSourceSnapshots.AnyAsync(item => item.Id == old.Id));
        Assert.False(await db.PlaylistSourceEntries.AnyAsync(item => item.PlaylistSourceSnapshotId == old.Id));
        Assert.Equal(fixture.Snapshots.TakeLast(2).Select(item => item.Id).Order(),
            (await db.PlaylistSourceSnapshots.Select(item => item.Id).ToArrayAsync()).Order());
        Assert.Equal(2, await db.ExternalMetadataSnapshots.CountAsync());
        Assert.True(await db.ExternalMetadataSnapshots.AnyAsync(item => item.Id == rotated.Id));
        Assert.Single(await db.ManualTrackOverrides.ToArrayAsync());
    }

    [Theory]
    [InlineData("run")]
    [InlineData("result")]
    [InlineData("job")]
    [InlineData("source-job")]
    [InlineData("one-time")]
    [InlineData("recent")]
    public async Task CleanupKeepsReferencedAndFrozenSourceVersions(string reference)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = new AllstarrDbContext(fixture.Database.Options);
        var old = fixture.Snapshots[0];
        if (reference == "recent")
            await db.PlaylistSourceSnapshots.Where(item => item.Id == old.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RetrievedAt, DateTimeOffset.UtcNow));
        else if (reference == "one-time")
            (await db.PlaylistLinks.SingleAsync()).ImportMode = PlaylistImportMode.OneTime;
        else if (reference is "job" or "source-job")
        {
            var job = new DurableJobRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = fixture.Owner,
                Type = "playlist.sync",
                IdempotencyKey = "refresh",
                State = DurableJobState.Pending,
                PayloadJson = JsonSerializer.Serialize(new { SourceSnapshotId = old.Id })
            };
            db.Jobs.Add(job);
            if (reference == "source-job")
            {
                job.PayloadJson = "{}";
                await db.SaveChangesAsync();
                await db.PlaylistSourceSnapshots.Where(item => item.Id == old.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SourceJobId, job.Id));
            }
        }
        else
        {
            var run = new PlaylistSyncRunRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = fixture.Owner,
                PlaylistLinkId = fixture.Link,
                PlaylistSourceSnapshotId = reference == "run" ? old.Id : fixture.Snapshots[^1].Id,
                Generation = 1,
                IdempotencyKey = "sync",
                State = PlaylistSyncState.Succeeded
            };
            db.PlaylistSyncRuns.Add(run);
            if (reference == "result")
                db.PlaylistSyncEntryResults.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    PlaylistSyncRunId = run.Id,
                    PlaylistSourceEntryId = await db.PlaylistSourceEntries.Where(item => item.PlaylistSourceSnapshotId == old.Id).Select(item => item.Id).SingleAsync(),
                    SourcePosition = 0,
                    Outcome = PlaylistEntryOutcome.Added
                });
        }
        await db.SaveChangesAsync();

        await PlaylistSnapshotRetention.PruneSourcesAsync(db, fixture.Link, default);

        Assert.True(await db.PlaylistSourceSnapshots.AnyAsync(item => item.Id == old.Id));
        Assert.Single(await db.PlaylistSourceEntries.Where(item => item.PlaylistSourceSnapshotId == old.Id).ToArrayAsync());
        Assert.Single(await db.ExternalMetadataSnapshots.ToArrayAsync());
    }

    [Fact]
    public async Task CleanupBoundsEntriesAndVersionsAndMakesProgressAcrossPasses()
    {
        await using var fixture = await Fixture.CreateAsync(versions: 14, oldEntries: 501);
        await using var db = new AllstarrDbContext(fixture.Database.Options);
        var beforeEntries = await db.PlaylistSourceEntries.CountAsync();
        var beforeVersions = await db.PlaylistSourceSnapshots.CountAsync();
        await PlaylistSnapshotRetention.PruneSourcesAsync(db, fixture.Link, default);
        Assert.InRange(beforeEntries - await db.PlaylistSourceEntries.CountAsync(), 1, 500);
        Assert.InRange(beforeVersions - await db.PlaylistSourceSnapshots.CountAsync(), 0, 10);
        Assert.True(await db.PlaylistSourceSnapshots.AnyAsync(item => item.Id == fixture.Snapshots[0].Id));
        for (var pass = 0; pass < 3; pass++)
            await PlaylistSnapshotRetention.PruneSourcesAsync(db, fixture.Link, default);
        Assert.Equal(2, await db.PlaylistSourceSnapshots.CountAsync());
        Assert.Equal(2, await db.PlaylistSourceEntries.CountAsync());
        Assert.Single(await db.ExternalMetadataSnapshots.ToArrayAsync());
    }

    [Fact]
    public async Task DocumentCleanupIsBoundedAndKeepsThreeVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = new AllstarrDbContext(fixture.Database.Options);
        for (var version = 1; version <= 105; version++)
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = fixture.Owner,
                ProviderId = "spotify",
                ResourceKind = "service-feed-songs",
                ExternalIdHash = Hash("feed"),
                SnapshotVersion = version,
                PayloadSha256 = Hash("[]"),
                PayloadJson = "[]"
            });
        await db.SaveChangesAsync();
        var obsolete = db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "service-feed-songs" && item.SnapshotVersion <= 102);
        Assert.Equal(100, await PlaylistSnapshotRetention.PruneDocumentsAsync(obsolete, default));
        Assert.Equal(2, await PlaylistSnapshotRetention.PruneDocumentsAsync(obsolete, default));
        Assert.Equal(new[] { 103, 104, 105 }, await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "service-feed-songs")
            .OrderBy(item => item.SnapshotVersion).Select(item => item.SnapshotVersion).ToArrayAsync());
        Assert.Single(await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "track").ToArrayAsync());
    }

    private sealed class Fixture(SqliteTestDatabase database) : IAsyncDisposable
    {
        public SqliteTestDatabase Database { get; } = database;
        public Guid Owner { get; } = Guid.CreateVersion7();
        public Guid Link { get; } = Guid.CreateVersion7();
        public List<PlaylistSourceSnapshotRecord> Snapshots { get; } = [];

        public static async Task<Fixture> CreateAsync(int versions = 3, int oldEntries = 1)
        {
            var fixture = new Fixture(await SqliteTestDatabase.CreateAsync());
            await using var db = new AllstarrDbContext(fixture.Database.Options);
            db.Users.Add(new() { Id = fixture.Owner, DisplayName = "Listener", Enabled = true });
            db.PlaylistLinks.Add(new()
            {
                Id = fixture.Link,
                OwnerUserId = fixture.Owner,
                SourceProviderId = "spotify",
                SourcePlaylistId = "feed",
                SourcePlaylistIdHash = Hash("feed"),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "fixture"
            });
            var song = new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = fixture.Owner,
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = Hash("song"),
                SnapshotVersion = 1,
                PayloadJson = "{}",
                PayloadSha256 = Hash("{}")
            };
            db.ExternalMetadataSnapshots.Add(song);
            for (var version = 1; version <= versions; version++)
            {
                var snapshot = new PlaylistSourceSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = fixture.Owner,
                    PlaylistLinkId = fixture.Link,
                    SnapshotVersion = version,
                    RetrievedAt = DateTimeOffset.UtcNow.AddDays(-2),
                    PublishedAt = DateTimeOffset.UtcNow.AddDays(-2),
                    Name = "Feed",
                    PayloadSha256 = Hash(version.ToString())
                };
                fixture.Snapshots.Add(snapshot);
                db.PlaylistSourceSnapshots.Add(snapshot);
                for (var position = 0; position < (version == 1 ? oldEntries : 1); position++)
                    db.PlaylistSourceEntries.Add(new()
                    {
                        Id = Guid.CreateVersion7(),
                        PlaylistSourceSnapshotId = snapshot.Id,
                        ExternalMetadataSnapshotId = song.Id,
                        SourcePosition = position,
                        SourceEntryIdHash = Hash(version + ":" + position)
                    });
            }
            await db.SaveChangesAsync();
            return fixture;
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
