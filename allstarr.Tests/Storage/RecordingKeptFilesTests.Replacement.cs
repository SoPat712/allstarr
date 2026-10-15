using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using allstarr.Core.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("replaced")]
    [InlineData("symlink")]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ANewHeartRepairsUnavailableKeptBytesAndPreservesEveryInterest(string damage)
    {
        var initial = await PublishInitialFavoriteAsync();
        var otherIntent = await SeedFavoriteIntentAsync(other, CanonicalItemId());
        Assert.NotNull(await RepairService().RetainFavoriteAsync(
            new(other, otherIntent.JobId, otherIntent.AttemptNumber)));
        File.Delete(initial.Path);
        var untrusted = new byte[audio.Length];
        var outside = Path.Combine(root, "unowned-replacement.flac");
        if (damage == "corrupt") await File.WriteAllBytesAsync(initial.Path, untrusted);
        if (damage == "replaced") await File.WriteAllBytesAsync(initial.Path, audio);
        if (damage == "symlink")
        {
            await File.WriteAllBytesAsync(outside, untrusted);
            File.CreateSymbolicLink(initial.Path, outside);
        }
        Assert.Null(await RepairService().FindAsync(Actor(other), recordingId));
        var current = await PrepareReplacementFavoriteAsync();

        var repaired = await RepairService().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{current.StateId:N}",
            Metadata = new("Restored title", ["Artist"])
        });

        Assert.NotEqual(initial.ManagedFileId, repaired.ManagedFileId);
        Assert.NotEqual(initial.Path, repaired.Path);
        Assert.Equal(audio, await File.ReadAllBytesAsync(repaired.Path));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        Assert.Equal(repaired.ManagedFileId, (await RepairService().FindAsync(Actor(other), recordingId))!.ManagedFileId);
        await using var db = new AllstarrDbContext(database.Options);
        var old = await db.ManagedFiles.SingleAsync(item => item.Id == initial.ManagedFileId);
        Assert.NotNull(old.RemovedAt);
        Assert.Equal(0, old.ReferenceCount);
        Assert.Equal(repaired.ManagedFileId, (await db.Set<RecordingKeptRouteEntity>().SingleAsync()).ManagedFileId);
        var active = await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync();
        Assert.Equal(2, active.Length);
        Assert.All(active, item => Assert.Equal(repaired.ManagedFileId, item.ManagedFileId));
        Assert.Contains(active, item => item.ReferenceKey == $"heart:{current.StateId:N}");
        Assert.Contains(active, item => item.ReferenceKey == $"heart:{otherIntent.StateId:N}");
        Assert.False(await db.ManagedFileReferences.AnyAsync(item =>
            item.ManagedFileId == initial.ManagedFileId && item.ReleasedAt == null));
        var cleanup = Assert.Single(await db.Jobs.Where(item =>
            item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(DurableJobState.Pending, cleanup.State);
        Assert.Equal(owner, cleanup.OwnerUserId);
        Assert.Equal(initial.ManagedFileId, System.Text.Json.JsonSerializer
            .Deserialize<KeptRecordingCleanupPayload>(cleanup.PayloadJson)!.ManagedFileId);
        var cleanupClaim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync(
            "repaired-retirement", [RecordingKeptFiles.KeptCleanupJobType]));
        var completion = await RepairService().CleanupAsync(cleanupClaim);
        Assert.Equal(damage == "missing" ? DurableJobCompletionKind.Succeeded : DurableJobCompletionKind.Failed,
            completion.Kind);
        if (damage == "missing") Assert.False(File.Exists(initial.Path));
        if (damage == "corrupt") Assert.Equal(untrusted, await File.ReadAllBytesAsync(initial.Path));
        if (damage == "replaced") Assert.Equal(audio, await File.ReadAllBytesAsync(initial.Path));
        if (damage == "symlink")
        {
            Assert.True((File.GetAttributes(initial.Path) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(untrusted, await File.ReadAllBytesAsync(outside));
        }
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RepairMovesAllRoutesAndInterestsSharingTheSameVerifiedContent()
    {
        var firstRecording = recordingId;
        var initial = await PublishInitialFavoriteAsync();
        recordingId = (await identities.CreateRecordingAsync(Actor(owner), "second-kept-recording")).Recording.Id;
        var secondRecording = recordingId;
        await identities.LinkAsync(SourceContext(), new(recordingId,
            new("deezer", ProviderResourceKind.Track, "second-source"), ProviderIdentityScope.Catalog,
            ProviderIdentityVerification.Verified, "fixture-exact", 1));
        var second = await PrepareReplacementFavoriteAsync("second-source");
        var shared = await RepairService().PublishAsync(Request() with
        {
            ReferenceKey = $"heart:{second.StateId:N}",
            Metadata = new("Second title", ["Artist"])
        });
        Assert.Equal(initial.ManagedFileId, shared.ManagedFileId);
        File.Delete(initial.Path);
        recordingId = firstRecording;
        var replacement = await PrepareReplacementFavoriteAsync();

        var repaired = await RepairService().PublishAsync(Request() with { ReferenceKey = $"heart:{replacement.StateId:N}" });

        await using var db = new AllstarrDbContext(database.Options);
        var routes = await db.Set<RecordingKeptRouteEntity>().ToArrayAsync();
        Assert.Equal(2, routes.Length);
        Assert.All(routes, route => Assert.Equal(repaired.ManagedFileId, route.ManagedFileId));
        var references = await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync();
        Assert.Equal(2, references.Length);
        Assert.All(references, reference => Assert.Equal(repaired.ManagedFileId, reference.ManagedFileId));
        Assert.NotNull((await db.ManagedFiles.SingleAsync(item => item.Id == initial.ManagedFileId)).RemovedAt);
        Assert.Equal("Second title", (await RepairService().FindSongAsync(Actor(other),
            CanonicalRecordingRecord.DefaultPublicId(secondRecording)))!.Title);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CanonicalRepairReusesAHealthyMergedAliasAndItsProvenance()
    {
        var firstRecording = recordingId;
        var initial = await PublishInitialFavoriteAsync();
        recordingId = (await identities.CreateRecordingAsync(Actor(owner), "healthy-alias")).Recording.Id;
        var aliasRecording = recordingId;
        await identities.LinkAsync(SourceContext(), new(recordingId,
            new("deezer", ProviderResourceKind.Track, "alias-source"), ProviderIdentityScope.Catalog,
            ProviderIdentityVerification.Verified, "fixture-exact", 1));
        var alternateBytes = "fLaCverified alternate encoding fixture audio"u8.ToArray();
        var aliasIntent = await PrepareReplacementFavoriteAsync("alias-source", alternateBytes);
        var alias = await RepairService().PublishAsync(Request() with { ReferenceKey = $"heart:{aliasIntent.StateId:N}" });
        var aliasArtifact = artifact.Id;
        Assert.NotEqual(initial.ManagedFileId, alias.ManagedFileId);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var merged = await db.CanonicalRecordings.SingleAsync(item => item.Id == aliasRecording);
            merged.MergedIntoId = firstRecording;
            merged.Revision++;
            await db.SaveChangesAsync();
        }
        File.Delete(initial.Path);
        recordingId = firstRecording;
        var current = await PrepareReplacementFavoriteAsync();

        var repaired = await RepairService().PublishAsync(Request() with { ReferenceKey = $"heart:{current.StateId:N}" });

        Assert.Equal(alias.ManagedFileId, repaired.ManagedFileId);
        Assert.Equal(alternateBytes, await File.ReadAllBytesAsync(repaired.Path));
        await using var verify = new AllstarrDbContext(database.Options);
        var canonicalRoute = await verify.Set<RecordingKeptRouteEntity>().SingleAsync(item =>
            item.CanonicalRecordingId == firstRecording);
        Assert.Equal(aliasArtifact, canonicalRoute.SourceArtifactId);
        Assert.NotNull((await verify.ManagedFiles.SingleAsync(item => item.Id == initial.ManagedFileId)).RemovedAt);
        Assert.Equal(2, await verify.ManagedFileReferences.CountAsync(item =>
            item.ManagedFileId == alias.ManagedFileId && item.ReleasedAt == null));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RevocationDuringRepairLeavesTheOldRouteAndEveryInterestIntact()
    {
        var initial = await PublishInitialFavoriteAsync();
        File.Delete(initial.Path);
        var current = await PrepareReplacementFavoriteAsync();
        var boundaryReached = false;
        ArmBoundary(db =>
        {
            boundaryReached = true;
            db.ProviderAccounts.Single(item => item.Id == account).Enabled = false;
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RepairService().PublishAsync(
            Request() with { ReferenceKey = $"heart:{current.StateId:N}" }));

        Assert.True(boundaryReached);
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Equal(initial.ManagedFileId, (await verify.Set<RecordingKeptRouteEntity>().SingleAsync()).ManagedFileId);
        var old = await verify.ManagedFiles.SingleAsync(item => item.Id == initial.ManagedFileId);
        Assert.Null(old.RemovedAt);
        Assert.Equal(1, old.ReferenceCount);
        Assert.Equal(initial.ManagedFileId, (await verify.ManagedFileReferences.SingleAsync(item =>
            item.ReleasedAt == null)).ManagedFileId);
        var provisional = await verify.ManagedFiles.Where(item => item.Id != initial.ManagedFileId).ToArrayAsync();
        Assert.Single(provisional);
        Assert.All(provisional, item => Assert.NotNull(item.RemovedAt));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Lane", "ReleaseCritical")]
    public async Task DifferentEncodingRepairRequiresEverySharedRouteToResolveToTheSameRecording(bool merged)
    {
        var firstRecording = recordingId;
        var initial = await PublishInitialFavoriteAsync();
        recordingId = (await identities.CreateRecordingAsync(Actor(owner), "shared-encoding-recording")).Recording.Id;
        var secondRecording = recordingId;
        await identities.LinkAsync(SourceContext(), new(recordingId,
            new("deezer", ProviderResourceKind.Track, "shared-encoding-source"), ProviderIdentityScope.Catalog,
            ProviderIdentityVerification.Verified, "fixture-exact", 1));
        var second = await PrepareReplacementFavoriteAsync("shared-encoding-source");
        var shared = await RepairService().PublishAsync(Request() with { ReferenceKey = $"heart:{second.StateId:N}" });
        Assert.Equal(initial.ManagedFileId, shared.ManagedFileId);
        if (merged)
        {
            await using var db = new AllstarrDbContext(database.Options);
            var alias = await db.CanonicalRecordings.SingleAsync(item => item.Id == secondRecording);
            alias.MergedIntoId = firstRecording;
            alias.Revision++;
            await db.SaveChangesAsync();
        }
        File.Delete(initial.Path);
        recordingId = firstRecording;
        var changedEncoding = "fLaCnew verified encoding of the same recording"u8.ToArray();
        var current = await PrepareReplacementFavoriteAsync(sourceBytes: changedEncoding);
        var request = Request() with { ReferenceKey = $"heart:{current.StateId:N}" };
        if (!merged)
        {
            await Assert.ThrowsAsync<IOException>(() => RepairService().PublishAsync(request));
            await using var unchanged = new AllstarrDbContext(database.Options);
            Assert.All(await unchanged.Set<RecordingKeptRouteEntity>().ToArrayAsync(),
                item => Assert.Equal(initial.ManagedFileId, item.ManagedFileId));
            Assert.Null((await unchanged.ManagedFiles.SingleAsync(item => item.Id == initial.ManagedFileId)).RemovedAt);
            Assert.Equal(2, await unchanged.ManagedFileReferences.CountAsync(item =>
                item.ManagedFileId == initial.ManagedFileId && item.ReleasedAt == null));
            return;
        }

        var repaired = await RepairService().PublishAsync(request);

        Assert.NotEqual(initial.ManagedFileId, repaired.ManagedFileId);
        Assert.Equal(changedEncoding, await File.ReadAllBytesAsync(repaired.Path));
        await using var verify = new AllstarrDbContext(database.Options);
        var routes = await verify.Set<RecordingKeptRouteEntity>().ToArrayAsync();
        Assert.Equal(2, routes.Length);
        Assert.All(routes, route =>
        {
            Assert.Equal(repaired.ManagedFileId, route.ManagedFileId);
            Assert.Equal(artifact.Id, route.SourceArtifactId);
            Assert.Equal(jobId, route.PublicationJobId);
        });
        Assert.Equal(2, await verify.ManagedFileReferences.CountAsync(item =>
            item.ManagedFileId == repaired.ManagedFileId && item.ReleasedAt == null));
    }

    private RecordingKeptFiles RepairService() => new(factory, artifacts,
        services.GetRequiredService<IServiceScopeFactory>(), placement, identities, new SystemPlatformClock(), jobs: queue);

    private async Task<FavoriteFixture> PrepareReplacementFavoriteAsync(
        string sourceTrack = "source-track", byte[]? sourceBytes = null)
    {
        var current = await SeedFavoriteIntentAsync(owner, CanonicalItemId());
        jobId = current.JobId;
        var workspace = await artifacts.CreateWorkspaceAsync(new(owner, jobId, "deezer", account, "replacement-source"));
        await using var input = new MemoryStream(sourceBytes ?? audio);
        var written = await artifacts.WriteAsync(new(workspace.Reference, jobId, "deezer", "source.flac", input, 1000));
        artifact = await artifacts.ResolveAsync(workspace.Reference,
            new(written.ArtifactId, written.Sha256, written.SizeBytes, new("audio/flac", "flac", "flac"), true),
            sourceTrack: new("deezer", ProviderResourceKind.Track, sourceTrack), sourceAccountRevision: 7);
        return current;
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeepingTheSameBytesAfterGraceDeletionPreservesTheOldRemovalRecord()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "replace-off"));
        clock.UtcNow = clock.UtcNow.AddHours(24);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("replace", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);
        Assert.False(File.Exists(published.Path));

        await using var scope = services.CreateAsyncScope();
        var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
            new(placement.RootId, placement.RootPath, null), artifact.SourcePath, placement.PathTemplate,
            Request().Track, null, ManagedFileScopeKey.Create(null, placement.RootId), true)
        { ReferenceKey = "keep:replacement", DestinationIsImmutable = true });

        Assert.NotEqual(published.ManagedFileId, placed.File.Id);
        Assert.NotEqual(published.Path, placed.File.CanonicalPath);
        Assert.Equal(audio, await File.ReadAllBytesAsync(placed.File.CanonicalPath));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        Assert.True(await scope.ServiceProvider.GetRequiredService<ManagedFileRemovalService>()
            .ResumeUnpublishedRemovalAsync(published.ManagedFileId,
                ManagedFileScopeKey.Create(null, placement.RootId)));
        Assert.Equal(audio, await File.ReadAllBytesAsync(placed.File.CanonicalPath));
        var db = scope.ServiceProvider.GetRequiredService<allstarr.Core.Storage.AllstarrDbContext>();
        Assert.Equal(2, await db.ManagedFiles.CountAsync());
        Assert.NotNull((await db.ManagedFiles.SingleAsync(item => item.Id == published.ManagedFileId)).RemovedAt);
        Assert.Null((await db.ManagedFiles.SingleAsync(item => item.Id == placed.File.Id)).RemovedAt);
    }
}
