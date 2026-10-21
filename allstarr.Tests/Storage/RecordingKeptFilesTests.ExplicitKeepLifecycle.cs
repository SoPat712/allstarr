using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task ExplicitKeepImmediatelyProtectsACopyAndRepeatedRequestIdsDoNotDuplicateWork()
    {
        await SeedKeepCacheAsync();
        var payload = await SeedPlaylistInterestAsync();
        var file = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var (_, _, _, persistence) = PlaylistInterestServices();
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false));
        var controller = KeepController(linked: true);
        controller.Request.Headers["Idempotency-Key"] = Guid.CreateVersion7().ToString("D");
        var first = Assert.IsType<AcceptedResult>(await controller.PromoteCachedDownload("cached.flac"));
        var second = Assert.IsType<AcceptedResult>(await controller.PromoteCachedDownload("cached.flac"));
        Assert.Equal(JsonSerializer.SerializeToElement(first.Value).GetProperty("jobId").GetGuid(),
            JsonSerializer.SerializeToElement(second.Value).GetProperty("jobId").GetGuid());
        await using var db = new AllstarrDbContext(database.Options);
        var reference = Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(file.ManagedFileId, reference.ManagedFileId);
        Assert.Equal($"keep:user:{owner:N}:{recordingId:N}", reference.ReferenceKey);
        Assert.Single(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.ExplicitKeepJobType && item.State == DurableJobState.Pending).ToArrayAsync());
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredExplicitKeepWorkerPublishesVerifiedHouseholdAudioOrReusesItOffline(bool existing)
    {
        await SeedKeepCacheAsync();
        if (existing) await Service().PublishAsync(Request());
        var claim = await AcceptKeepAsync();
        if (!existing) await CreateKeepArtifactAsync(claim);
        else await DisableSourceAccountAsync();
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        if (!existing)
        {
            var descriptor = ExecutorDescriptor();
            providers.Setup(item => item.FindByCapability(ProviderCapabilityKind.Download, true)).Returns([descriptor]);
            router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(It.Is<ProviderRouteRequest>(request =>
                request.OperationId == "recording-keep"))).ReturnsAsync(ExecutorPlan(descriptor));
        }
        var collection = new ServiceCollection().AddLogging();
        collection.AddProviderDownloadArtifacts(new ConfigurationBuilder().Build());
        collection.AddSingleton<IProviderRouter>(router.Object);
        collection.AddSingleton<IProviderRegistry>(providers.Object);
        collection.AddSingleton<IPlatformClock>(new SystemPlatformClock());
        collection.AddSingleton(artifacts);
        collection.AddSingleton(ExplicitKeepService());
        collection.AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory);
        collection.AddSingleton(queue);
        using var qualityCache = new allstarr.Services.Common.FileMediaApplicationCache(
            new allstarr.Services.Common.FileMediaCacheOptions(Path.Combine(root, "quality-cache")),
            new SystemPlatformClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Services.Common.FileMediaApplicationCache>.Instance);
        collection.AddSingleton(qualityCache);
        await using var registered = collection.BuildServiceProvider();
        var handler = registered.GetServices<IDurableJobHandler>().Single(item => item.JobType == RecordingKeptFiles.ExplicitKeepJobType);
        var result = await handler.ExecuteAsync(new(claim, registered), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        if (existing)
        {
            router.VerifyNoOtherCalls();
            providers.VerifyNoOtherCalls();
        }
        var kept = Assert.IsType<KeptRecordingFile>(await Service().FindAsync(Actor(other), recordingId));
        Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
        Assert.Equal(audio, await File.ReadAllBytesAsync(Path.Combine(root, "cache", "cached.flac")));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.All(await db.Set<ProviderDownloadArtifactEntity>().ToArrayAsync(), item => Assert.Equal(ProviderDownloadArtifactState.Verified, item.State));
        Assert.Contains(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync(),
            item => item.ReferenceKey == $"keep:user:{owner:N}:{recordingId:N}");
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("role")]
    [InlineData("principal")]
    [InlineData("attempt")]
    [InlineData("recording")]
    public async Task ExplicitKeepPublicationRejectsAChangedAcceptedRequest(string change)
    {
        await SeedKeepCacheAsync();
        var claim = await AcceptKeepAsync();
        var source = await CreateKeepArtifactAsync(claim);
        if (change == "attempt")
        {
            await using (var finish = new AllstarrDbContext(database.Options))
                await finish.Jobs.Where(item => item.Id == jobId).ExecuteUpdateAsync(set => set.SetProperty(item => item.State, DurableJobState.Succeeded));
            await queue.RecoverInterruptedAsync();
            var next = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("new-keep", [RecordingKeptFiles.ExplicitKeepJobType]));
            Assert.Equal(claim.JobId, next.JobId);
            Assert.Equal(2, next.AttemptNumber);
        }
        else
        {
            var replacement = change == "recording" ? (await identities.CreateRecordingAsync(Actor(owner), "replacement-keep")).Recording.Id : Guid.Empty;
            await using var update = new AllstarrDbContext(database.Options);
            if (change == "cancelled") (await update.Jobs.SingleAsync(item => item.Id == claim.JobId)).CancellationRequestedAt = DateTimeOffset.UtcNow;
            else if (change == "role") (await update.Users.SingleAsync(item => item.Id == owner)).IsAdmin = false;
            else if (change == "principal") (await update.Users.SingleAsync(item => item.Id == owner)).BackendPrincipalId = "changed-principal";
            else (await update.ProviderTrackIdentities.SingleAsync()).CanonicalRecordingId = replacement;
            await update.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExplicitKeepService().PublishRequestedKeepAsync(claim, source));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.DoesNotContain(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync(),
            item => item.ReferenceKey.StartsWith("keep:user:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitKeepPublicationRechecksCancellationAndRoleAfterPreparingBytes(bool role)
    {
        await SeedKeepCacheAsync();
        var claim = await AcceptKeepAsync();
        var source = await CreateKeepArtifactAsync(claim);
        placementBoundary = () =>
        {
            using var update = new AllstarrDbContext(database.Options);
            if (role) update.Users.Single(item => item.Id == owner).IsAdmin = false;
            else update.Jobs.Single(item => item.Id == claim.JobId).CancellationRequestedAt = DateTimeOffset.UtcNow;
            update.SaveChanges();
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExplicitKeepService().PublishRequestedKeepAsync(claim, source));
        Assert.Null(placementBoundary);
        Assert.Equal(audio, await File.ReadAllBytesAsync(source.SourcePath));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.DoesNotContain(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync(),
            item => item.ReferenceKey.StartsWith("keep:user:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReleasingExplicitKeepUsesGraceAndPreservesOtherHeartReferences()
    {
        await SeedKeepCacheAsync();
        var claim = await AcceptKeepAsync();
        var source = await CreateKeepArtifactAsync(claim);
        var file = await ExplicitKeepService().PublishRequestedKeepAsync(claim, source);
        await queue.CompleteAsync(claim, DurableJobCompletion.Success());
        var (_, _, _, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(other, CanonicalItemId(), FavoriteOperation.Favorite, "other-keeps"));
        var controller = KeepController(linked: true);
        var list = Assert.IsType<OkObjectResult>(await controller.GetDownloads());
        var entry = Assert.Single(JsonSerializer.SerializeToElement(list.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("files").EnumerateArray());
        Assert.True(entry.GetProperty("explicitlyKept").GetBoolean());
        Assert.True(entry.GetProperty("removable").GetBoolean());
        Assert.IsType<AcceptedResult>(await controller.DeleteDownload(entry.GetProperty("path").GetString()!));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.DoesNotContain(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync(),
            item => item.ReferenceKey.StartsWith("keep:user:", StringComparison.Ordinal));
        Assert.StartsWith("heart:", Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync()).ReferenceKey);
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public async Task ExplicitKeepReleaseSchedulesGraceAndFreshKeepCancelsItBeforeAnotherWorkerRuns()
    {
        await SeedKeepCacheAsync();
        var claim = await AcceptKeepAsync();
        var file = await ExplicitKeepService().PublishRequestedKeepAsync(claim, await CreateKeepArtifactAsync(claim));
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(1, await ExplicitKeepService().ReleaseKeepForFileAsync(KeepContext(owner), file.ManagedFileId));
        await using (var released = new AllstarrDbContext(database.Options))
        {
            Assert.Equal(0, (await released.ManagedFiles.SingleAsync()).ReferenceCount);
            var cleanup = Assert.Single(await released.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
            Assert.InRange(cleanup.AvailableAt, before.AddHours(24), DateTimeOffset.UtcNow.AddHours(24));
            Assert.NotNull((await released.Jobs.SingleAsync(item => item.Id == claim.JobId)).CancellationRequestedAt);
            Assert.Equal(DurableJobState.Running, (await released.Jobs.SingleAsync(item => item.Id == claim.JobId)).State);
        }
        Assert.Equal(audio, await File.ReadAllBytesAsync(file.Path));
        Assert.IsType<AcceptedResult>(await KeepController(linked: true).PromoteCachedDownload("cached.flac"));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(1, (await db.ManagedFiles.SingleAsync()).ReferenceCount);
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)).State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExplicitKeepService().RetainRequestedKeepAsync(claim));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitKeepReleaseRequiresCurrentAdminAndNeverReleasesAnotherUsersInterest(bool demoted)
    {
        await SeedKeepCacheAsync();
        var claim = await AcceptKeepAsync();
        var file = await ExplicitKeepService().PublishRequestedKeepAsync(claim, await CreateKeepArtifactAsync(claim));
        await using (var update = new AllstarrDbContext(database.Options))
        {
            (await update.Users.SingleAsync(item => item.Id == (demoted ? owner : other))).IsAdmin = !demoted;
            await update.SaveChangesAsync();
        }
        if (demoted)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExplicitKeepService().ReleaseKeepForFileAsync(KeepContext(owner), file.ManagedFileId));
        else
        {
            var context = KeepContext(other);
            Assert.Equal(0, await ExplicitKeepService().ReleaseKeepForFileAsync(context, file.ManagedFileId));
        }
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(1, (await db.ManagedFiles.SingleAsync()).ReferenceCount);
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
    }

    [Fact]
    public async Task BulkRemovalReleasesExplicitKeepAtConfiguredRootWithoutDeletingItsAudio()
    {
        await SeedKeepCacheAsync();
        placement.RootPath = Path.Combine(root, "custom-household");
        var claim = await AcceptKeepAsync();
        var file = await ExplicitKeepService().PublishRequestedKeepAsync(claim, await CreateKeepArtifactAsync(claim));
        var controller = KeepController(linked: true);
        var list = Assert.IsType<OkObjectResult>(await controller.GetDownloads());
        Assert.Equal(1, JsonSerializer.SerializeToElement(list.Value).GetProperty("count").GetInt32());
        var result = Assert.IsType<OkObjectResult>(await controller.DeleteAllDownloads());
        Assert.Equal(1, JsonSerializer.SerializeToElement(result.Value).GetProperty("releasedCount").GetInt32());
        Assert.Equal(audio, await File.ReadAllBytesAsync(file.Path));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Single(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
    }

    [Fact]
    public async Task ExplicitKeepAcceptanceRollsBackReferenceAndCleanupCancellationWhenQueueSaveFails()
    {
        await SeedKeepCacheAsync();
        var payload = await SeedPlaylistInterestAsync();
        await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var (_, _, _, persistence) = PlaylistInterestServices();
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false));
        await using (var failure = new AllstarrDbContext(database.Options))
            await failure.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_explicit_keep BEFORE INSERT ON durable_jobs
                WHEN NEW.Type = 'recording.keep'
                BEGIN SELECT RAISE(ABORT, 'fixture queue unavailable'); END;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(() => ExplicitKeepService().RequestKeepAsync(KeepContext(owner),
            new("deezer", ProviderResourceKind.Track, "source-track"), new("Title", ["Artist"]), Guid.CreateVersion7()));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(DurableJobState.Pending, (await db.Jobs.SingleAsync(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)).State);
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.ExplicitKeepJobType && item.State == DurableJobState.Pending).ToArrayAsync());
    }

    [Fact]
    public async Task ExplicitKeepRequestIdCannotBeReboundToDifferentMetadata()
    {
        await SeedKeepCacheAsync();
        var key = Guid.CreateVersion7();
        var first = await ExplicitKeepService().RequestKeepAsync(KeepContext(owner),
            new("deezer", ProviderResourceKind.Track, "source-track"), new("First", ["Artist"]), key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExplicitKeepService().RequestKeepAsync(KeepContext(owner),
            new("deezer", ProviderResourceKind.Track, "source-track"), new("Different", ["Artist"]), key));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(first.JobId, Assert.Single(await db.Jobs.Where(item => item.State == DurableJobState.Pending).ToArrayAsync()).Id);
    }

    [Fact]
    public async Task ExplicitKeepPreservesTheSupportedBackendInstanceLength()
    {
        await SeedKeepCacheAsync();
        var instance = new string('b', 200);
        await using (var update = new AllstarrDbContext(database.Options))
        {
            (await update.Users.SingleAsync(item => item.Id == owner)).BackendInstanceId = instance;
            await update.SaveChangesAsync();
        }
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, instance, owner.ToString("N"),
            new(owner, "jellyfin", instance, owner.ToString("N"), "Keeper", true), "long-instance",
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var accepted = await ExplicitKeepService().RequestKeepAsync(execution,
            new("deezer", ProviderResourceKind.Track, "source-track"), new("Title", ["Artist"]), Guid.CreateVersion7());
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("long-instance", [RecordingKeptFiles.ExplicitKeepJobType]));
        Assert.Equal(accepted.JobId, claim.JobId);
        Assert.Null(await ExplicitKeepService().RetainRequestedKeepAsync(claim));
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("revision")]
    [InlineData("scope")]
    [InlineData("account-owner")]
    public async Task ExplicitKeepRechecksCacheInputInsideAcceptance(string change)
    {
        await SeedKeepCacheAsync();
        CachedKeepSource proof;
        await using (var update = new AllstarrDbContext(database.Options))
        {
            var mapping = await update.DownloadedSongMappings.SingleAsync();
            proof = new(mapping.Id, mapping.Revision);
            if (change == "removed") update.Remove(mapping);
            else if (change == "revision") mapping.Revision++;
            else if (change == "scope") mapping.ScopeKey = "changed-scope";
            else (await update.ProviderAccounts.SingleAsync(item => item.Id == account)).OwnerUserId = other;
            await update.SaveChangesAsync();
        }
        var exception = await Record.ExceptionAsync(() => ExplicitKeepService().RequestKeepAsync(KeepContext(owner),
            new("deezer", ProviderResourceKind.Track, "source-track"), new("Title", ["Artist"]), Guid.CreateVersion7(), cachedSource: proof));
        Assert.NotNull(exception);
        Assert.True(exception is InvalidOperationException or UnauthorizedAccessException);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.ExplicitKeepJobType && item.State == DurableJobState.Pending).ToArrayAsync());
        Assert.Empty(await db.ManagedFiles.ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(Path.Combine(root, "cache", "cached.flac")));
    }

    private static ProtocolExecutionContext KeepContext(Guid userId) => new(ProtocolKind.Jellyfin, "fixture", userId.ToString("N"),
        new(userId, "jellyfin", "fixture", userId.ToString("N"), "Keeper", true), "explicit-keep",
        DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private RecordingKeptFiles ExplicitKeepService() => new(factory, artifacts, services.GetRequiredService<IServiceScopeFactory>(),
        placement, identities, new SystemPlatformClock(), jobs: queue);

    private async Task<DurableJobClaim> AcceptKeepAsync()
    {
        Assert.IsType<AcceptedResult>(await KeepController(linked: true).PromoteCachedDownload("cached.flac"));
        return Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("explicit-keep", [RecordingKeptFiles.ExplicitKeepJobType]));
    }

    private async Task<VerifiedProviderDownloadArtifact> CreateKeepArtifactAsync(DurableJobClaim claim)
    {
        var workspace = await artifacts.CreateWorkspaceAsync(new(owner, claim.JobId, "deezer", account, "explicit-source"));
        await using var input = new MemoryStream(audio);
        var written = await artifacts.WriteAsync(new(workspace.Reference, claim.JobId, "deezer", "keep.flac", input, 1000));
        return await artifacts.ResolveAsync(workspace.Reference,
            new(written.ArtifactId, written.Sha256, written.SizeBytes, new("audio/flac", "flac", "flac"), true),
            sourceTrack: new("deezer", ProviderResourceKind.Track, "source-track"), sourceAccountRevision: 7);
    }
}
