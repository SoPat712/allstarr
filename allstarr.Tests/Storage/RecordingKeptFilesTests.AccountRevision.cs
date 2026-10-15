using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("unused")]
    [InlineData("published")]
    [InlineData("reserved")]
    public async Task AccountRevisionRetryGetsFreshBytesWithoutLosingKeptProof(string use)
    {
        var published = use == "published" ? await Service().PublishAsync(Request()) : null;
        var reservation = use == "reserved" ? await PlaceReservationAsync(ReservationKey()) : null;
        await using (var edit = new AllstarrDbContext(database.Options))
        {
            var source = await edit.ProviderAccounts.SingleAsync(item => item.Id == account);
            source.Revision = 8;
            source.UpdatedAt = DateTimeOffset.UtcNow;
            var job = await edit.Jobs.SingleAsync(item => item.Id == jobId);
            job.AttemptCount = 2;
            job.Revision++;
            await edit.SaveChangesAsync();
        }
        var capability = new RevisionDownloadCapability(artifacts);
        var download = RevisionDownloadService(capability, revision: 8);

        var result = await download.ExecuteAsync(new(owner, jobId, "deezer", "source-track", "revision-retry",
            "revision-retry", 2, "recording-keep"));

        Assert.True(result.Succeeded, result.ErrorCode);
        var fresh = Assert.IsType<VerifiedProviderDownloadArtifact>(result.Artifact);
        Assert.Equal(1, capability.AvailabilityCalls);
        Assert.Equal(1, capability.DownloadCalls);
        Assert.Equal(8, fresh.SourceAccountRevision);
        Assert.Equal("source-track", fresh.SourceExternalTrackId);
        Assert.NotEqual(artifact.Id, fresh.Id);
        Assert.NotEqual(artifact.WorkspaceRecordId, fresh.WorkspaceRecordId);
        Assert.NotEqual(artifact.ContentSha256, fresh.ContentSha256);
        Assert.Equal(RevisionDownloadCapability.Bytes, await File.ReadAllBytesAsync(fresh.SourcePath));
        Assert.Equal(use != "unused", File.Exists(artifact.SourcePath));
        await using (var read = new AllstarrDbContext(database.Options))
        {
            var records = await read.ProviderDownloadArtifacts.AsNoTracking().Where(item => item.DurableJobId == jobId).ToArrayAsync();
            Assert.Equal(2, records.Length);
            var previous = records.Single(item => item.Id == artifact.Id);
            Assert.Equal(7, previous.SourceAccountRevision);
            Assert.Equal(artifact.ContentSha256, previous.ContentSha256);
            Assert.NotNull(previous.SupersededAt);
            Assert.Equal(ProviderDownloadArtifactState.Verified, previous.State);
        }
        if (published != null)
        {
            var kept = Assert.IsType<KeptRecordingFile>(await Service().FindAsync(Actor(other), recordingId));
            Assert.Equal(published.ManagedFileId, kept.ManagedFileId);
            Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
            await using var read = new AllstarrDbContext(database.Options);
            Assert.Equal(artifact.Id, (await read.Set<RecordingKeptRouteEntity>().SingleAsync()).SourceArtifactId);
        }
        if (reservation != null)
        {
            var (clock, jobs, kept, _) = CreateInterestPipeline();
            await SetJobStateAsync(DurableJobState.Failed, clock);
            Assert.Equal(1, await kept.RecoverReservationsAsync());
            await using var read = new AllstarrDbContext(database.Options);
            Assert.Empty(await read.ManagedFileReferences.Where(item => item.ManagedFileId == reservation.File.Id &&
                item.ReleasedAt == null).ToArrayAsync());
            var cleanup = Assert.Single(await read.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
            clock.UtcNow = cleanup.AvailableAt;
            var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("revision-reservation-cleanup",
                [RecordingKeptFiles.KeptCleanupJobType]));
            Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);
            Assert.False(File.Exists(artifact.SourcePath));
            Assert.Equal(RevisionDownloadCapability.Bytes, await File.ReadAllBytesAsync(fresh.SourcePath));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("superseded")]
    public async Task AccountRevisionRetryRecoversWithoutOpeningStaleBytes(string source)
    {
        await AdvanceDownloadRevisionAsync();
        if (source == "missing") File.Delete(artifact.SourcePath);
        if (source == "corrupt") await File.WriteAllTextAsync(artifact.SourcePath, "damaged");
        if (source == "superseded")
            Assert.True(await new EfProviderDownloadArtifactStore(factory).SupersedeAsync(artifact, 8, 2,
                DateTimeOffset.UtcNow, default));
        var capability = new RevisionDownloadCapability(artifacts);
        var result = await RevisionDownloadService(capability, 8).ExecuteAsync(RevisionCommand());
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(1, capability.DownloadCalls);
        Assert.False(File.Exists(artifact.SourcePath));
        Assert.Equal(RevisionDownloadCapability.Bytes, await File.ReadAllBytesAsync(result.Artifact!.SourcePath));
        var retry = await RevisionDownloadService(capability, 8).ExecuteAsync(RevisionCommand());
        Assert.Equal(result.Artifact.Id, retry.Artifact!.Id);
        Assert.Equal(1, capability.DownloadCalls);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("disabled")]
    [InlineData("owner")]
    [InlineData("attempt")]
    [InlineData("cancel")]
    public async Task AccountRevisionRetryRejectsStalePlanWithoutDiscardingProof(string changed)
    {
        await AdvanceDownloadRevisionAsync();
        await using (var edit = new AllstarrDbContext(database.Options))
        {
            var saved = await edit.ProviderAccounts.SingleAsync(item => item.Id == account);
            var job = await edit.Jobs.SingleAsync(item => item.Id == jobId);
            if (changed == "revision") saved.Revision = 9;
            if (changed == "disabled") saved.Enabled = false;
            if (changed == "owner") saved.OwnerUserId = other;
            if (changed == "attempt") job.AttemptCount = 3;
            if (changed == "cancel") job.CancellationRequestedAt = DateTimeOffset.UtcNow;
            await edit.SaveChangesAsync();
        }
        var capability = new RevisionDownloadCapability(artifacts);
        var result = await RevisionDownloadService(capability, 8).ExecuteAsync(RevisionCommand());
        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Equal("managed_download_account_changed", result.ErrorCode);
        Assert.Equal(0, capability.DownloadCalls);
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        await using var read = new AllstarrDbContext(database.Options);
        Assert.Null((await read.ProviderDownloadArtifacts.SingleAsync()).SupersededAt);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task DownloadGenerationUpgradePreservesPublishedProofAndRefusesLossyDowngrade()
    {
        const string before = "20261015090448_RetainKeptRecordingDisplayMetadata";
        var published = await Service().PublishAsync(Request());
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        var triggers = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name").ToArrayAsync();
        await migrator.MigrateAsync(before);
        Assert.Single(await db.Database.SqlQueryRaw<string>(
            "SELECT Id AS Value FROM provider_download_artifacts").ToArrayAsync());
        await db.Database.MigrateAsync();
        Assert.Null((await db.ProviderDownloadArtifacts.AsNoTracking().SingleAsync()).SupersededAt);
        Assert.Equal(published.ManagedFileId, (await Service().FindAsync(Actor(other), recordingId))!.ManagedFileId);
        await AdvanceDownloadRevisionAsync();
        Assert.True((await RevisionDownloadService(new(artifacts), 8).ExecuteAsync(RevisionCommand())).Succeeded);
        var rejected = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => migrator.MigrateAsync(before));
        Assert.Contains("CK_download_history_requires_backup_restore", rejected.Message, StringComparison.Ordinal);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(2, await db.ProviderDownloadArtifacts.CountAsync());
        Assert.Single(await db.ProviderDownloadArtifacts.Where(item => item.SupersededAt != null).ToArrayAsync());
        Assert.Equal(triggers, await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name").ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [Fact]
    public async Task AccountRevisionRetryRejectsOlderPlanWithoutChangingTheNewerArtifact()
    {
        var capability = new RevisionDownloadCapability(artifacts);
        var result = await RevisionDownloadService(capability, 6).ExecuteAsync(RevisionCommand() with { Attempt = 1 });
        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Equal("managed_download_account_changed", result.ErrorCode);
        Assert.Equal(0, capability.DownloadCalls);
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        await using var read = new AllstarrDbContext(database.Options);
        Assert.Null((await read.ProviderDownloadArtifacts.SingleAsync()).SupersededAt);
    }

    [Fact]
    public async Task SupersededWorkspaceCleanupRejectsSymlinksAndPreservesTheirTarget()
    {
        await AdvanceDownloadRevisionAsync();
        var outside = Path.Combine(root, "outside-source.flac");
        await File.WriteAllTextAsync(outside, "unrelated bytes");
        File.Delete(artifact.SourcePath);
        File.CreateSymbolicLink(artifact.SourcePath, outside);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            RevisionDownloadService(new(artifacts), 8).ExecuteAsync(RevisionCommand()));
        Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(outside));
        Assert.NotNull(new FileInfo(artifact.SourcePath).LinkTarget);
        await using var read = new AllstarrDbContext(database.Options);
        Assert.Single(await read.ProviderDownloadArtifacts.ToArrayAsync());
        Assert.NotNull((await read.ProviderDownloadArtifacts.SingleAsync()).SupersededAt);
    }

    private ManagedTrackDownloadCommand RevisionCommand() =>
        new(owner, jobId, "deezer", "source-track", "revision-retry", "revision-retry", 2, "recording-keep");

    private async Task AdvanceDownloadRevisionAsync()
    {
        await using var edit = new AllstarrDbContext(database.Options);
        (await edit.ProviderAccounts.SingleAsync(item => item.Id == account)).Revision = 8;
        var job = await edit.Jobs.SingleAsync(item => item.Id == jobId);
        job.AttemptCount = 2;
        job.Revision++;
        await edit.SaveChangesAsync();
    }

    private ManagedTrackDownloadService RevisionDownloadService(RevisionDownloadCapability capability, long revision)
    {
        var clock = new SystemPlatformClock();
        var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null, durableJobId: jobId, actingForUserId: owner);
        var policy = new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
            ProviderExplicitContentPolicy.Allow, true, false, true);
        var track = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "source-track");
        var descriptor = new ProviderDescriptor("deezer", "Fixture download", "Fixture download", ProviderOrigin.BuiltIn, "1", "1.0",
            [new(ProviderCapabilityKind.Download, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Optional,
                "1.0", ["checkAvailability", "download"], [ProviderAccountScope.Personal])], new ProviderPermissionDescriptor());
        var request = new ProviderRouteRequest(ProviderCapabilityKind.Download, actor, policy, "recording-keep", "revision-retry",
            clock.UtcNow.AddHours(1), ["deezer"], [new("deezer", availableQualities: Enum.GetValues<ProviderAudioQuality>())],
            track, "revision-retry");
        var context = new ProviderExecutionContext(actor, "deezer", new(account, "deezer", ProviderAccountScope.Personal,
            revision, ownerUserId: owner), policy, "recording-keep", "revision-retry", clock.UtcNow.AddHours(1), default, "revision-retry");
        var candidate = new ProviderRouteCandidate<IProviderDownloadCapability>(0, descriptor, descriptor.Capabilities.Single(),
            capability, context, track);
        var plan = new ProviderRoutePlan<IProviderDownloadCapability>(request, [candidate], new(request.CorrelationId,
            ProviderCapabilityKind.Download, "deezer", account, [new("deezer", account, ProviderRouteDecisionStatus.Accepted, "selected", 0)]));
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(It.IsAny<ProviderRouteRequest>())).ReturnsAsync(plan);
        var registry = new Mock<IProviderRegistry>(MockBehavior.Strict);
        registry.Setup(item => item.FindByCapability(ProviderCapabilityKind.Download, true)).Returns([descriptor]);
        return new(router.Object, registry.Object, artifacts, clock, NullLogger<ManagedTrackDownloadService>.Instance);
    }

    private sealed class RevisionDownloadCapability(ProviderDownloadArtifactResolver artifacts) : IProviderDownloadCapability
    {
        public static readonly byte[] Bytes = Encoding.UTF8.GetBytes("fLaCfresh account revision fixture audio");
        public string ProviderId => "deezer";
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Download;
        public int AvailabilityCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(ProviderExecutionContext context,
            ProviderDownloadAvailabilityRequest request)
        {
            AvailabilityCalls++;
            return Task.FromResult(ProviderOutcome<ProviderDownloadAvailability>.Success(new(ProviderDownloadAvailabilityState.Available)));
        }

        public async Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(ProviderExecutionContext context,
            ProviderDownloadRequest request, IProgress<ProviderDownloadProgress>? progress = null)
        {
            DownloadCalls++;
            await using var input = new MemoryStream(Bytes, writable: false);
            var written = await artifacts.WriteAsync(new(request.Workspace, request.DurableJobId, ProviderId, "track.flac", input, 1024),
                context.CancellationToken);
            return ProviderOutcome<ProviderDownloadedArtifact>.Success(new(written.ArtifactId, written.Sha256, written.SizeBytes,
                new("audio/flac", "flac", "flac"), true));
        }
    }
}
