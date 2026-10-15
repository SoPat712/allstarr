using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task InterruptedReservationIsRecoveredWhenItsRetryFailsBeforePublication()
    {
        var reservation = await PlaceReservationAsync(ReservationKey());
        var (clock, jobs, kept, _) = CreateInterestPipeline();
        Assert.True(await jobs.RecoverInterruptedAsync() >= 1);
        Assert.Equal(0, await kept.RecoverReservationsAsync());
        var retry = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("resumed", ["recording.keep"]));
        Assert.Equal(2, retry.AttemptNumber);
        await jobs.CompleteAsync(retry, DurableJobCompletion.Retry("fixture", "Retry later.", TimeSpan.FromSeconds(1)));
        var recovery = (IDurableJobRecoveryHandler)new KeptRecordingCleanupJobHandler(kept);
        await recovery.RecoverCompletedAsync(retry, default);
        await using (var pending = new AllstarrDbContext(database.Options))
            Assert.Single(await pending.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var final = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("final", ["recording.keep"]));
        await jobs.CompleteAsync(final, DurableJobCompletion.Failure("fixture", "No source available."));
        await recovery.RecoverCompletedAsync(retry, default);
        await using (var stale = new AllstarrDbContext(database.Options))
            Assert.Single(await stale.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        await recovery.RecoverCompletedAsync(final with { OwnerUserId = other }, default);
        await recovery.RecoverCompletedAsync(final with { Type = "playback.signal" }, default);
        await using (var wrongContext = new AllstarrDbContext(database.Options))
            Assert.Single(await wrongContext.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        await recovery.RecoverCompletedAsync(final, default);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(DurableJobState.Failed, (await db.Jobs.SingleAsync(item => item.Id == jobId)).State);
        Assert.Single(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(reservation.File.CanonicalPath));
    }

    [Fact]
    public async Task CompletionRecoveryReleasesOnlyItsExactJobsReservations()
    {
        var firstFile = await PlaceReservationAsync(ReservationKey());
        var (_, jobs, kept, _) = CreateInterestPipeline();
        await jobs.RecoverInterruptedAsync();
        var first = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("first", ["recording.keep"]));
        var secondId = (await jobs.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "recording.keep", "second-reservation", new { }, owner))).JobId;
        var second = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("second", ["recording.keep"]));
        Assert.Equal(secondId, second.JobId);
        var workspace = await artifacts.CreateWorkspaceAsync(new(owner, secondId, "deezer", account, "second-source"));
        await using var input = new MemoryStream(audio);
        var written = await artifacts.WriteAsync(new(workspace.Reference, secondId, "deezer", "source.flac", input, 1000));
        await artifacts.ResolveAsync(workspace.Reference,
            new(written.ArtifactId, written.Sha256, written.SizeBytes, new("audio/flac", "flac", "flac"), true),
            sourceTrack: new("deezer", allstarr.Core.Capabilities.ProviderResourceKind.Track, "source-track"), sourceAccountRevision: 7);
        var secondKey = $"pending:kept:{secondId:N}:{recordingId:N}:1:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Request().ReferenceKey)));
        Assert.Equal(firstFile.File.Id, (await PlaceReservationAsync(secondKey)).File.Id);
        await jobs.CompleteAsync(first, DurableJobCompletion.Failure("fixture", "First stopped."));
        await jobs.CompleteAsync(second, DurableJobCompletion.Failure("fixture", "Second stopped."));
        var recovery = (IDurableJobRecoveryHandler)new KeptRecordingCleanupJobHandler(kept);
        await recovery.RecoverCompletedAsync(first, default);
        await using (var afterFirst = new AllstarrDbContext(database.Options))
        {
            Assert.Equal(secondKey, (await afterFirst.ManagedFileReferences.SingleAsync(item => item.ReleasedAt == null)).ReferenceKey);
            Assert.Empty(await afterFirst.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        }
        await recovery.RecoverCompletedAsync(second, default);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Single(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }
}
