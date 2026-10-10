using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class CancelLegacyRematchJobsMigrationTests
{
    private const string Before = "20261010061013_RecordingIdentifiers";

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradeCancelsRetiredRematchJobTypes()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var playlistJob = Guid.CreateVersion7();
        var allJob = Guid.CreateVersion7();
        var keepJob = Guid.CreateVersion7();
        var emptyJson = "{}";
        var firstFingerprint = new string('a', 64);
        var secondFingerprint = new string('b', 64);
        var keepFingerprint = new string('c', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id,BackendType,BackendInstanceId,BackendPrincipalId,DisplayName,IsAdmin,Enabled,CreatedAt,UpdatedAt,LastSeenAt)
            VALUES ({user},'jellyfin','fixture','listener','Listener',0,1,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks});
            INSERT INTO durable_jobs (Id,ScopeKey,OwnerUserId,PolicySnapshotJson,RequestFingerprint,CorrelationId,Type,PayloadJson,IdempotencyKey,State,Priority,AttemptCount,FailureCount,DeferralCount,MaxAttempts,MaxDeferrals,AvailableAt,CreatedAt,UpdatedAt,Revision)
            VALUES ({playlistJob},'playlist',{user},{emptyJson},{firstFingerprint},'playlist-job','playlist.rematch',{emptyJson},'playlist-job','Pending',0,0,0,0,3,3,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks},1);
            INSERT INTO durable_jobs (Id,ScopeKey,OwnerUserId,PolicySnapshotJson,RequestFingerprint,CorrelationId,Type,PayloadJson,IdempotencyKey,State,Priority,AttemptCount,FailureCount,DeferralCount,MaxAttempts,MaxDeferrals,AvailableAt,CreatedAt,UpdatedAt,Revision)
            VALUES ({allJob},'track',{user},{emptyJson},{secondFingerprint},'all-job','track-match.rematch-all',{emptyJson},'all-job','Pending',0,0,0,0,3,3,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks},1);
            INSERT INTO durable_jobs (Id,ScopeKey,OwnerUserId,PolicySnapshotJson,RequestFingerprint,CorrelationId,Type,PayloadJson,IdempotencyKey,State,Priority,AttemptCount,FailureCount,DeferralCount,MaxAttempts,MaxDeferrals,AvailableAt,CreatedAt,UpdatedAt,Revision)
            VALUES ({keepJob},'playlist',{user},{emptyJson},{keepFingerprint},'keep-job','playlist.materialize',{emptyJson},'keep-job','Pending',0,0,0,0,3,3,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks},1);
            """);

        await db.Database.MigrateAsync();

        Assert.Equal("Cancelled", await db.Jobs.Where(item => item.Id == playlistJob).Select(item => item.State.ToString()).SingleAsync());
        Assert.Equal("Cancelled", await db.Jobs.Where(item => item.Id == allJob).Select(item => item.State.ToString()).SingleAsync());
        Assert.Equal("Pending", await db.Jobs.Where(item => item.Id == keepJob).Select(item => item.State.ToString()).SingleAsync());
    }
}
