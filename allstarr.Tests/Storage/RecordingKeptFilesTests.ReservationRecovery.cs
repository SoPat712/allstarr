using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(DurableJobState.Cancelled, false)]
    [InlineData(DurableJobState.Failed, false)]
    [InlineData(DurableJobState.Succeeded, false)]
    [InlineData(DurableJobState.Cancelled, true)]
    public async Task TerminalReservationRecoveryReleasesAndQueuesCleanup(
        DurableJobState terminalState, bool disableInitiator)
    {
        var reservation = await PlaceReservationAsync(ReservationKey());
        var (clock, jobs, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(terminalState, clock);
        if (disableInitiator) await DisableOwnerAsync();

        Assert.Equal(1, await kept.RecoverReservationsAsync());

        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == reservation.File.Id && item.ReleasedAt == null).ToArrayAsync());
        var cleanup = Assert.Single(await db.Jobs.Where(item =>
            item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        var payload = JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(cleanup.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(recordingId, payload!.RecordingId);
        Assert.Equal(reservation.File.Id, payload.ManagedFileId);
        Assert.Equal(jobId, payload.ReservationJobId);
        Assert.Equal(DurableJobState.Pending, cleanup.State);
        Assert.Equal(clock.UtcNow.AddHours(24), cleanup.AvailableAt);
        Assert.Null(cleanup.ProviderAccountId);
        Assert.Null(cleanup.ProviderCapability);
        Assert.Equal(0, await kept.RecoverReservationsAsync());
        Assert.Single(await db.Jobs.Where(item =>
            item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());

        if (disableInitiator)
        {
            clock.UtcNow = cleanup.AvailableAt;
            var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync(
                "reservation-disabled", [RecordingKeptFiles.KeptCleanupJobType]));
            Assert.True((await jobs.ReauthorizeAsync(claim)).Authorized);
            await jobs.CompleteAsync(claim, DurableJobCompletion.Success());
        }
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RecoveredReservationCleanupDeletesOnlyUnpublishedBytes()
    {
        var reservation = await PlaceReservationAsync(ReservationKey());
        var (clock, jobs, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(DurableJobState.Succeeded, clock);
        Assert.Equal(1, await kept.RecoverReservationsAsync());

        await using (var beforeDue = new AllstarrDbContext(database.Options))
        {
            var pending = Assert.Single(await beforeDue.Jobs.Where(item =>
                item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
            Assert.Null(await jobs.ClaimNextAsync("reservation-wrong-type", [FavoriteActionPipeline.JobType]));
            clock.UtcNow = pending.AvailableAt;
        }

        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync(
            "reservation-cleanup", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(RecordingKeptFiles.KeptCleanupJobType, claim.Type);
        var completion = await kept.CleanupAsync(claim);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await jobs.CompleteAsync(claim, completion);

        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFiles.Where(item => item.Id == reservation.File.Id && item.RemovedAt == null)
            .ToArrayAsync());
        Assert.Empty(await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == reservation.File.Id && item.ReleasedAt == null).ToArrayAsync());
        Assert.False(File.Exists(reservation.File.CanonicalPath));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Theory]
    [InlineData(DurableJobState.Pending)]
    [InlineData(DurableJobState.RetryScheduled)]
    [InlineData(DurableJobState.Running)]
    public async Task NonterminalReservationRemainsActive(DurableJobState state)
    {
        var reservation = await PlaceReservationAsync(ReservationKey());
        var (clock, _, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(state, clock);

        Assert.Equal(0, await kept.RecoverReservationsAsync());
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Single(await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == reservation.File.Id && item.ReleasedAt == null).ToArrayAsync());
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)
            .ToArrayAsync());
        Assert.True(File.Exists(reservation.File.CanonicalPath));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("stale-attempt")]
    public async Task InvalidReservationKeyOrAttemptRemainsUntouched(string kind)
    {
        var key = kind == "malformed" ? "pending:kept:invalid" : ReservationKey(2);
        var reservation = await PlaceReservationAsync(key);
        var (clock, _, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(DurableJobState.Succeeded, clock);

        Assert.Equal(0, await kept.RecoverReservationsAsync());
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Single(await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == reservation.File.Id && item.ReleasedAt == null).ToArrayAsync());
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)
            .ToArrayAsync());
        Assert.True(File.Exists(reservation.File.CanonicalPath));
    }

    [Fact]
    public async Task LastingGenericReferenceProtectsTerminalReservation()
    {
        var reservation = await PlaceReservationAsync(ReservationKey());
        await using (var scope = services.CreateAsyncScope())
        {
            var ownership = scope.ServiceProvider.GetRequiredService<IManagedFileOwnershipStore>();
            await ownership.AddReferenceAsync(reservation.File.Id, new(
                Guid.CreateVersion7(), reservation.File.Id, null,
                ManagedFileScopeKey.Create(null, placement.RootId), "playlist:fixture:lasting",
                DateTimeOffset.UtcNow), default);
        }
        var (clock, _, kept, _) = CreateInterestPipeline();
        await SetJobStateAsync(DurableJobState.Succeeded, clock);

        Assert.Equal(1, await kept.RecoverReservationsAsync());
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == reservation.File.Id && item.ReferenceKey.StartsWith("pending:kept:") &&
            item.ReleasedAt == null).ToArrayAsync());
        Assert.Contains("playlist:fixture:lasting", await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == reservation.File.Id && item.ReleasedAt == null)
            .Select(item => item.ReferenceKey).ToArrayAsync());
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)
            .ToArrayAsync());
        Assert.True(File.Exists(reservation.File.CanonicalPath));
    }

    private async Task<ManagedFilePlacementResult> PlaceReservationAsync(string referenceKey)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
            new(placement.RootId, Path.GetFullPath(placement.RootPath), null), artifact.SourcePath,
            placement.PathTemplate, Request().Track, null,
            ManagedFileScopeKey.Create(null, placement.RootId), true, true,
            artifact.ContentSha256, artifact.Length)
        {
            ReferenceKey = referenceKey,
            DestinationIsImmutable = true
        });
    }

    private string ReservationKey(int attempt = 1, string? sourceReference = null) =>
        $"pending:kept:{jobId:N}:{recordingId:N}:{attempt}:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceReference ?? Request().ReferenceKey)));

    private async Task SetJobStateAsync(DurableJobState state, InterestClock clock)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var job = await db.Jobs.SingleAsync(item => item.Id == jobId);
        job.State = state;
        job.UpdatedAt = clock.UtcNow;
        job.Revision++;
        if (state is DurableJobState.Succeeded or DurableJobState.Failed or DurableJobState.Cancelled)
        {
            job.CompletedAt = clock.UtcNow;
            job.LastAttemptCompletedAt = clock.UtcNow;
        }
        else
        {
            job.CompletedAt = null;
            job.LastAttemptCompletedAt = null;
        }
        await db.SaveChangesAsync();
    }

    private async Task DisableOwnerAsync()
    {
        await using var db = new AllstarrDbContext(database.Options);
        (await db.Users.SingleAsync(item => item.Id == owner)).Enabled = false;
        await db.SaveChangesAsync();
    }
}
