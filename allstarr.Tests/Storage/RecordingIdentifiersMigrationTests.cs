using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class RecordingIdentifiersMigrationTests
{
    private const string Before = "20261010035223_ProviderAccountSettings";
    private static readonly string[] Removed =
    [
        "canonical_artists", "canonical_catalog_aliases", "canonical_recording_artists",
        "canonical_release_group_artists", "canonical_release_groups", "canonical_releases",
        "canonical_release_tracks", "catalog_facts"
    ];

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradeBackfillsPublicIdsAndIdentifiersThenDropsTheCatalogGraph()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        var keepJob = Guid.CreateVersion7();
        var emptyJson = "{}";
        var aliasHash = new string('a', 64);
        var catalogFingerprint = new string('b', 64);
        var keepFingerprint = new string('c', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id,BackendType,BackendInstanceId,BackendPrincipalId,DisplayName,IsAdmin,Enabled,CreatedAt,UpdatedAt,LastSeenAt)
            VALUES ({user},'jellyfin','fixture','listener','Listener',0,1,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks});
            INSERT INTO canonical_recordings (Id,CreatedByUserId,Title,IsProvisional,Isrc,MusicBrainzRecordingId,CreatedAt,UpdatedAt,Revision)
            VALUES ({recording},{user},'Song',0,'USRC17607839','11111111-1111-4111-8111-111111111111',{now.UtcTicks},{now.UtcTicks},1);
            INSERT INTO canonical_catalog_aliases (Id,EntityKind,CanonicalEntityId,Namespace,ExternalId,ExternalIdHash,CreatedAt,LastSeenAt)
            VALUES ({Guid.CreateVersion7()},'Recording',{recording},'isrc','USRC17607839',{aliasHash},{now.UtcTicks},{now.UtcTicks});
            INSERT INTO durable_jobs (Id,ScopeKey,OwnerUserId,PolicySnapshotJson,RequestFingerprint,CorrelationId,Type,PayloadJson,IdempotencyKey,State,Priority,AttemptCount,FailureCount,DeferralCount,MaxAttempts,MaxDeferrals,AvailableAt,CreatedAt,UpdatedAt,Revision)
            VALUES ({job},'catalog',{user},{emptyJson},{catalogFingerprint},'catalog-job','catalog.musicbrainz.refresh',{emptyJson},'catalog-job','Pending',0,0,0,0,3,3,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks},1);
            INSERT INTO durable_jobs (Id,ScopeKey,OwnerUserId,PolicySnapshotJson,RequestFingerprint,CorrelationId,Type,PayloadJson,IdempotencyKey,State,Priority,AttemptCount,FailureCount,DeferralCount,MaxAttempts,MaxDeferrals,AvailableAt,CreatedAt,UpdatedAt,Revision)
            VALUES ({keepJob},'playlist',{user},{emptyJson},{keepFingerprint},'keep-job','playlist.rematch',{emptyJson},'keep-job','Pending',0,0,0,0,3,3,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks},1);
            """);

        await db.Database.MigrateAsync();

        foreach (var table in Removed)
            Assert.False(await TableExists(db, table));
        Assert.True(await TableExists(db, "recording_identifiers"));
        Assert.Equal($"ext-allstarr-song-{recording:D}",
            await db.CanonicalRecordings.Where(item => item.Id == recording).Select(item => item.PublicId).SingleAsync());
        var identifiers = await db.RecordingIdentifiers.Where(item => item.RecordingId == recording)
            .OrderBy(item => item.Kind).ToArrayAsync();
        Assert.Equal(["isrc:USRC17607839", "musicbrainz:11111111-1111-4111-8111-111111111111"],
            identifiers.Select(item => $"{item.Kind}:{item.Value}").ToArray());
        Assert.Equal("Cancelled", await db.Jobs.Where(item => item.Id == job).Select(item => item.State.ToString()).SingleAsync());
        Assert.Equal("Pending", await db.Jobs.Where(item => item.Id == keepJob).Select(item => item.State.ToString()).SingleAsync());
        Assert.Equal(user, await db.Users.Select(item => item.Id).SingleAsync());
    }

    private static async Task<bool> TableExists(AllstarrDbContext db, string table) =>
        await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name={table}) AS Value").SingleAsync();
}
