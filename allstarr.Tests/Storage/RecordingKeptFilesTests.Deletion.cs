using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Routing;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task ConfirmedKeptDeletionUsesTheCurrentBackendAdministrator(ProtocolKind protocol)
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync(protocol);
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("backend-delete", [RecordingKeptFiles.KeptCleanupJobType]));

        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);

        Assert.False(File.Exists(published.Path));
        await using var db = factory.CreateDbContext();
        Assert.True((await db.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
    }

    [Fact]
    public async Task DeletedCopyCannotBeRepublishedByOlderWorkAfterACanonicalMerge()
    {
        var published = await Service().PublishAsync(Request());
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("merge-delete", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);
        var alias = (await identities.CreateRecordingAsync(Actor(owner), "deleted-copy-alias")).Recording.Id;
        await identities.MergeRecordingsAsync(Actor(owner), alias, recordingId, "deleted-copy-merge");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => kept.PublishAsync(Request() with { RecordingId = alias }));

        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
    }

    [Fact]
    public async Task DeletedFavoriteCopyDoesNotStartAnotherAutomaticProviderDownload()
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("suppressed-favorite", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        await using var db = factory.CreateDbContext();
        var favorite = await db.Set<FavoriteEventRecord>().SingleAsync();
        var action = DownloadAction(favorite);

        var result = await Executor(router.Object, providers.Object, gateway.Object)
            .ExecuteAsync(favorite, action, 1, default);

        Assert.True(result.Succeeded);
        router.VerifyNoOtherCalls();
        providers.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
        Assert.False(File.Exists(published.Path));
    }

    [Fact]
    public async Task DeletionEndpointsRequireAdministratorAndRejectAStalePreview()
    {
        var published = await PublishInitialFavoriteAsync();
        await DeletionAdministratorAsync();
        var path = Path.GetRelativePath(placement.RootPath, published.Path);
        Assert.IsType<UnauthorizedObjectResult>(await KeepController(linked: false).PreviewKeptDeletion(new(path)));
        var controller = KeepController(linked: true);
        var listing = Assert.IsType<OkObjectResult>(await controller.GetDownloads());
        var rows = System.Text.Json.JsonSerializer.SerializeToElement(listing.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).GetProperty("files");
        Assert.True(Assert.Single(rows.EnumerateArray()).GetProperty("canDeleteKeptCopy").GetBoolean());
        var response = Assert.IsType<OkObjectResult>(await controller.PreviewKeptDeletion(new(path)));
        var preview = Assert.IsType<KeptRecordingDeletionPreview>(response.Value);
        var request = DeleteConfirmation(preview);
        Assert.IsType<BadRequestObjectResult>(await controller.DeleteKeptCopy(request with { Confirmation = "yes" }));
        await File.WriteAllTextAsync(Path.ChangeExtension(published.Path, ".lrc"), "new lyrics after preview");

        Assert.IsType<ConflictObjectResult>(await controller.DeleteKeptCopy(request));

        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Null((await db.ManagedFiles.SingleAsync()).RemovedAt);
    }

    [Fact]
    public async Task ConfirmedDeletionReleasesPhysicalInterestsAndPreservesFavoriteAndDisplay()
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var sidecar = Path.ChangeExtension(published.Path, ".lrc");
        await File.WriteAllTextAsync(sidecar, "[00:01.000]Words");
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        Assert.Equal(1, preview.FavoriteReferences);
        var confirmation = DeleteConfirmation(preview);
        var queued = await kept.RequestDeletionAsync(execution, confirmation);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("confirmed-delete", [RecordingKeptFiles.KeptCleanupJobType]));

        var completion = await kept.CleanupAsync(claim);

        Assert.Equal(queued.JobId, claim.JobId);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await jobs.CompleteAsync(claim, completion);
        var repeated = await kept.RequestDeletionAsync(execution, confirmation);
        Assert.Equal(queued.JobId, repeated.JobId);
        Assert.False(repeated.Created);
        Assert.False(File.Exists(published.Path));
        Assert.False(File.Exists(sidecar));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        await using var db = factory.CreateDbContext();
        Assert.True((await db.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
        Assert.NotNull((await db.ManagedFiles.SingleAsync()).RemovedAt);
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.All(await db.ManagedFileReferences.ToArrayAsync(), item => Assert.NotNull(item.ReleasedAt));
        Assert.Null(await kept.FindAsync(Actor(other), recordingId));
        var display = Assert.IsType<allstarr.Models.Domain.Song>(await kept.FindSongAsync(Actor(other), CanonicalItemId()));
        Assert.Equal("Accepted title", display.Title);
        var favorite = await db.Set<FavoriteStateRecord>().SingleAsync();
        Assert.True(await RecordingKeptFiles.AutomaticRetentionSuppressedAsync(db, $"heart:{favorite.Id:N}", favorite.LastEventId));
        Assert.False(await RecordingKeptFiles.AutomaticRetentionSuppressedAsync(db, $"heart:{favorite.Id:N}", Guid.NewGuid()));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("new-reference")]
    [InlineData("new-favorite-generation")]
    [InlineData("disabled-admin")]
    [InlineData("changed-hash")]
    [InlineData("changed-inode")]
    [InlineData("symlink")]
    [InlineData("wrong-root")]
    public async Task ConfirmedDeletionRejectsChangedEvidenceBeforeRevocation(string change)
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        if (change == "new-reference")
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IManagedFileOwnershipStore>().AddReferenceAsync(published.ManagedFileId,
                new(Guid.NewGuid(), published.ManagedFileId, null, ManagedFileScopeKey.Create(null, placement.RootId),
                    "playlist:new-interest", DateTimeOffset.UtcNow), default);
        }
        else if (change == "new-favorite-generation") await SeedFavoriteIntentAsync(owner, CanonicalItemId());
        else if (change is "revision" or "disabled-admin")
        {
            await using var db = factory.CreateDbContext();
            if (change == "revision") (await db.ManagedFiles.SingleAsync()).Revision++;
            else (await db.Users.SingleAsync(item => item.Id == owner)).Enabled = false;
            await db.SaveChangesAsync();
        }
        else if (change == "wrong-root") placement.RootPath = Path.Combine(root, "unrelated-root");
        else
        {
            if (change == "changed-hash") await File.WriteAllTextAsync(published.Path, new string('x', audio.Length));
            else
            {
                File.Move(published.Path, published.Path + ".original");
                if (change == "symlink") File.CreateSymbolicLink(published.Path, published.Path + ".original");
                else await File.WriteAllBytesAsync(published.Path, audio);
            }
        }
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("changed-delete", [RecordingKeptFiles.KeptCleanupJobType]));

        var completion = await kept.CleanupAsync(claim);

        Assert.NotEqual(DurableJobCompletionKind.Succeeded, completion.Kind);
        await using var check = factory.CreateDbContext();
        Assert.Null((await check.ManagedFiles.SingleAsync()).RemovedAt);
        Assert.Single(await check.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.True(File.Exists(published.Path));
    }

    [Fact]
    public async Task ConfirmedDeletionPreservesPlaylistRowsAndFencesOldPublication()
    {
        var payload = await SeedPlaylistInterestAsync();
        var published = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var favorite = await SeedFavoriteIntentAsync(other, CanonicalItemId());
        Assert.NotNull(await Service().RetainFavoriteAsync(new(other, favorite.JobId, favorite.AttemptNumber)));
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        Assert.Equal(1, preview.PlaylistReferences);
        Assert.Equal(1, preview.FavoriteReferences);
        await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("playlist-delete", [RecordingKeptFiles.KeptCleanupJobType]));

        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PlaylistService().PublishAsync(PlaylistRequest(payload)));
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.PlaylistLinks.ToArrayAsync());
        Assert.Single(await db.PlaylistSourceEntries.ToArrayAsync());
        Assert.True((await db.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
        Assert.True(await RecordingKeptFiles.AutomaticRetentionSuppressedAsync(db, PlaylistRequest(payload).ReferenceKey));
    }

    [Fact]
    public async Task DeletionEvidenceAndDisplaySurviveNormalEventAndJobHistoryRetention()
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        var queued = await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("retention-delete", [RecordingKeptFiles.KeptCleanupJobType]));
        var completion = await kept.CleanupAsync(claim);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await jobs.CompleteAsync(claim, completion);

        await new AuditEventRetentionService(services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(), new DeletionFutureTimeProvider(),
            NullLogger<AuditEventRetentionService>.Instance).PruneNowAsync();

        await using var db = factory.CreateDbContext();
        Assert.True(await db.Jobs.AnyAsync(item => item.Id == queued.JobId));
        Assert.False(await db.AuditEvents.AnyAsync(item => item.Action == "deleted-copy"));
        var favorite = await db.Set<FavoriteStateRecord>().SingleAsync();
        Assert.True(await RecordingKeptFiles.AutomaticRetentionSuppressedAsync(db, $"heart:{favorite.Id:N}", favorite.LastEventId));
        Assert.Equal("Accepted title", (await kept.FindSongAsync(Actor(other), CanonicalItemId()))!.Title);
    }

    [Fact]
    public async Task RevokedDeletionResumesAfterPhysicalCleanupFailure()
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("retry-delete", [RecordingKeptFiles.KeptCleanupJobType]));
        Action? afterRevocation = null;
        afterRevocation = () =>
        {
            using var db = factory.CreateDbContext();
            if (db.ManagedFiles.Single().RemovedAt != null) throw new IOException("Physical cleanup interruption.");
            placementBoundary = afterRevocation;
        };
        placementBoundary = afterRevocation;

        var first = await kept.CleanupAsync(claim);

        Assert.Equal(DurableJobCompletionKind.Retry, first.Kind);
        Assert.True(File.Exists(published.Path));
        await using (var db = factory.CreateDbContext())
        {
            Assert.NotNull((await db.ManagedFiles.SingleAsync()).RemovedAt);
            Assert.Empty(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        }
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);
        Assert.False(File.Exists(published.Path));
    }

    [Fact]
    public async Task NewExplicitKeepCanRestoreDeletedBytesWhileOldFavoriteGenerationStaysSuppressed()
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        var deletion = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("restore-delete", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(deletion)).Kind);
        await kept.RequestKeepAsync(execution, new("deezer", allstarr.Core.Capabilities.ProviderResourceKind.Track, "source-track"),
            new("Restored title", ["Artist"]), Guid.NewGuid());
        var restore = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("restore-keep", [RecordingKeptFiles.ExplicitKeepJobType]));
        var workspace = await artifacts.CreateWorkspaceAsync(new(owner, restore.JobId, "deezer", account, "restore-source"));
        await using var input = new MemoryStream(audio);
        var written = await artifacts.WriteAsync(new(workspace.Reference, restore.JobId, "deezer", "restore.flac", input, 1000));
        var source = await artifacts.ResolveAsync(workspace.Reference,
            new(written.ArtifactId, written.Sha256, written.SizeBytes, new("audio/flac", "flac", "flac"), true),
            sourceTrack: new("deezer", allstarr.Core.Capabilities.ProviderResourceKind.Track, "source-track"), sourceAccountRevision: 7);

        var restored = await kept.PublishRequestedKeepAsync(restore, source);

        Assert.NotEqual(published.ManagedFileId, restored.ManagedFileId);
        Assert.Equal(audio, await File.ReadAllBytesAsync(restored.Path));
        Assert.NotNull(await kept.FindAsync(Actor(other), recordingId));
        await using var db = factory.CreateDbContext();
        var favorite = await db.Set<FavoriteStateRecord>().SingleAsync();
        Assert.True(await RecordingKeptFiles.AutomaticRetentionSuppressedAsync(db, $"heart:{favorite.Id:N}", favorite.LastEventId));
        Assert.Equal(1, await db.ManagedFileReferences.CountAsync(item => item.ManagedFileId == restored.ManagedFileId && item.ReleasedAt == null));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("replacement")]
    [InlineData("symlink")]
    public async Task ChangedLyricsAreLeftAloneAndRevokedRemovalCanResume(string change)
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, jobs, kept, _) = CreateInterestPipeline();
        var execution = await DeletionAdministratorAsync();
        var sidecar = Path.ChangeExtension(published.Path, ".lrc");
        await File.WriteAllTextAsync(sidecar, "original lyrics");
        var preview = await kept.PreviewDeletionAsync(execution, published.ManagedFileId);
        await kept.RequestDeletionAsync(execution, DeleteConfirmation(preview));
        var expected = "changed lyrics";
        if (change == "replacement")
        {
            File.Move(sidecar, sidecar + ".original");
            expected = "original lyrics";
            await File.WriteAllTextAsync(sidecar, expected);
        }
        else if (change == "symlink")
        {
            var target = Path.Combine(root, "unrelated.lrc");
            await File.WriteAllTextAsync(target, expected);
            File.Delete(sidecar);
            File.CreateSymbolicLink(sidecar, target);
        }
        else await File.WriteAllTextAsync(sidecar, expected);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("lyrics-delete", [RecordingKeptFiles.KeptCleanupJobType]));

        var completion = await kept.CleanupAsync(claim);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(expected, await File.ReadAllTextAsync(sidecar));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);
    }

    private async Task<ProtocolExecutionContext> DeletionAdministratorAsync(ProtocolKind protocol = ProtocolKind.Jellyfin)
    {
        await using var db = factory.CreateDbContext();
        var user = await db.Users.SingleAsync(item => item.Id == owner);
        user.IsAdmin = true;
        user.BackendType = protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic";
        await db.SaveChangesAsync();
        return new(protocol, "fixture", owner.ToString("N"),
            new(owner, user.BackendType, "fixture", owner.ToString("N"), "Keeper", true), "kept-delete",
            DateTimeOffset.UtcNow.AddMinutes(5), default);
    }

    private static KeptRecordingDeleteRequest DeleteConfirmation(KeptRecordingDeletionPreview preview) =>
        new(preview.ManagedFileId, preview.Revision, preview.ReferenceFingerprint, "Delete kept copy", Guid.NewGuid());

    private sealed class DeletionFutureTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(90);
    }
}
