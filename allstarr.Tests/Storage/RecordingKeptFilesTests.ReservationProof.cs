using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task RecoveryContinuesPastAFullPageOfUnrecognizedReferences()
    {
        var reservation = await PlaceReservationAsync(ReservationKey());
        var (clock, _, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(DurableJobState.Cancelled, clock);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            for (var index = 1; index <= 200; index++)
                db.ManagedFileReferences.Add(new ManagedFileReferenceEntity
                {
                    Id = Guid.Parse($"00000000-0000-0000-0000-{index:x12}"),
                    ManagedFileId = reservation.File.Id,
                    ScopeKey = reservation.File.ScopeKey,
                    ReferenceKey = $"pending:kept:unrecognized:{index}",
                    CreatedAt = clock.UtcNow,
                    Revision = 1
                });
            await db.SaveChangesAsync();
            Assert.All(await db.ManagedFileReferences.OrderBy(item => item.Id).Take(200).ToArrayAsync(),
                item => Assert.StartsWith("pending:kept:unrecognized:", item.ReferenceKey));
        }
        Assert.Equal(1, await kept.RecoverReservationsAsync());
        await using var fresh = new AllstarrDbContext(database.Options);
        Assert.Equal(200, await fresh.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
        Assert.Equal(0, await kept.RecoverReservationsAsync());
        Assert.Empty(await fresh.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
    }

    [Fact]
    public async Task ValidReservationCannotAuthorizeAnotherReferenceWithAFutureAttempt()
    {
        var valid = await PlaceReservationAsync(ReservationKey());
        var invalid = await PlaceReservationAsync(ReservationKey(2));
        Assert.Equal(valid.File.Id, invalid.File.Id);
        var (clock, _, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(DurableJobState.Cancelled, clock);
        Assert.Equal(1, await kept.RecoverReservationsAsync());
        await using var db = new AllstarrDbContext(database.Options);
        var remaining = Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(ReservationKey(2), remaining.ReferenceKey);
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(valid.File.CanonicalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationRecoveryRequiresTheOriginalBoundArtifact(bool missingTrackBinding)
    {
        var reservation = await PlaceReservationAsync(ReservationKey());
        var (clock, _, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(DurableJobState.Cancelled, clock);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var source = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
            if (missingTrackBinding) source.SourceExternalTrackId = null;
            else source.ContentSha256 = new string('b', 64);
            source.Revision++;
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await kept.RecoverReservationsAsync());
        await using var fresh = new AllstarrDbContext(database.Options);
        Assert.Single(await fresh.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(reservation.File.CanonicalPath));
        Assert.Empty(await fresh.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
    }

    [Fact]
    public async Task RegisteredCleanupHandlerRecoversWithoutRevivingTheSourceAccount()
    {
        var reservation = await PlaceReservationAsync(ReservationKey());
        var (clock, _, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(DurableJobState.Cancelled, clock);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.ProviderAccounts.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        }
        var recovery = Assert.IsAssignableFrom<IDurableJobRecoveryHandler>(new KeptRecordingCleanupJobHandler(kept));
        await recovery.RecoverAsync(default);
        await using var fresh = new AllstarrDbContext(database.Options);
        Assert.Empty(await fresh.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Single(await fresh.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.False((await fresh.ProviderAccounts.SingleAsync()).Enabled);
        Assert.Equal(audio, await File.ReadAllBytesAsync(reservation.File.CanonicalPath));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }
}
