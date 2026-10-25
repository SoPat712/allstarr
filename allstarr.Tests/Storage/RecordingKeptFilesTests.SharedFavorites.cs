using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    private const string SharedFavoriteItem = "ext-deezer-song-source-track";

    [Fact]
    public async Task SharedUnheartReleasesExactFavoriteReferencesAndPreservesOtherRetentionReasons()
    {
        var (first, second, linkService) = await SharedProfilesAsync();
        var original = await SeedFavoriteIntentAsync(owner, SharedFavoriteItem, jobId, realProviderIntent: true);
        var linked = await linkService.CreateAsync(FavoriteLinkTests.Fixture.Session(first, true), LinkRequest(first, second), default);
        var published = await SharedKept().PublishAsync(Request() with { ReferenceKey = $"heart:{original.StateId:N}" });
        var pipeline = SharedPipeline();
        await pipeline.RecordAsync(SharedMutation(second, true, "peer-heart"));
        var explicitKey = $"keep:user:{owner:N}:{recordingId:N}";
        var playlistKey = $"playlist:{Guid.NewGuid():N}:entry";
        await using (var db = new AllstarrDbContext(database.Options))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var store = new EfManagedFileOwnershipStore(db);
            await store.StageReferenceAsync(published.ManagedFileId, explicitKey, DateTimeOffset.UtcNow, default);
            await store.StageReferenceAsync(published.ManagedFileId, playlistKey, DateTimeOffset.UtcNow, default);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await pipeline.RecordAsync(SharedMutation(second, false, "peer-unheart"));

        await using var saved = new AllstarrDbContext(database.Options);
        var references = await saved.ManagedFileReferences.Where(item => item.ManagedFileId == published.ManagedFileId).ToArrayAsync();
        Assert.Equal(2, references.Count(item => item.ReleasedAt == null));
        Assert.All(references.Where(item => item.ReferenceKey.StartsWith("heart:", StringComparison.Ordinal)), item => Assert.NotNull(item.ReleasedAt));
        Assert.Contains(references, item => item.ReferenceKey == explicitKey && item.ReleasedAt == null);
        Assert.Contains(references, item => item.ReferenceKey == playlistKey && item.ReleasedAt == null);
        Assert.Equal(2, (await saved.ManagedFiles.SingleAsync()).ReferenceCount);
        Assert.NotNull((await saved.Jobs.SingleAsync(item => item.Id == original.JobId)).CancellationRequestedAt);
        Assert.False((await saved.Set<LinkedProviderFavoriteStateRecord>().SingleAsync()).IsFavorite);
        Assert.Empty(await saved.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
        Assert.Equal(linked.LinkId, (await saved.Set<ProviderFavoriteLinkRecord>().SingleAsync()).Id);
    }

    [Fact]
    public async Task OldPeerUnheartCannotReleaseAReplacementPrincipalsPrivateFavoriteReference()
    {
        var (first, second, linkService) = await SharedProfilesAsync();
        var original = await SeedFavoriteIntentAsync(owner, SharedFavoriteItem, jobId, realProviderIntent: true);
        await linkService.CreateAsync(FavoriteLinkTests.Fixture.Session(first, true), LinkRequest(first, second), default);
        var published = await SharedKept().PublishAsync(Request() with { ReferenceKey = $"heart:{original.StateId:N}" });
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).BackendPrincipalId = "replacement";
            await db.SaveChangesAsync();
        }
        first = FavoriteLinkTests.Fixture.Copy(first, principal: "replacement");
        var pipeline = SharedPipeline();
        var replacement = await pipeline.RecordAsync(SharedMutation(first, true, "private-heart"));

        await pipeline.RecordAsync(SharedMutation(second, false, "old-peer-unheart"));

        await using var saved = new AllstarrDbContext(database.Options);
        var state = await saved.FavoriteStates.SingleAsync(item => item.OwnerUserId == owner);
        Assert.Equal(original.StateId, state.Id);
        Assert.Equal(replacement.EventId, state.LastEventId);
        Assert.True(state.IsFavorite);
        var accepted = await saved.FavoriteEvents.SingleAsync(item => item.Id == replacement.EventId);
        Assert.True(accepted.IsRealProviderIntent);
        Assert.Null(accepted.SharedLinkId);
        Assert.Null((await saved.Jobs.SingleAsync(item => item.Id == replacement.JobId)).CancellationRequestedAt);
        var reference = Assert.Single(await saved.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal($"heart:{state.Id:N}", reference.ReferenceKey);
        Assert.Equal(published.ManagedFileId, reference.ManagedFileId);
        Assert.False((await saved.Set<LinkedProviderFavoriteStateRecord>().SingleAsync()).IsFavorite);
        Assert.Single(await new FavoriteViewReader(factory).ReadAsync(FavoriteLinkTests.Fixture.Context(first)));
    }

    [Fact]
    public async Task SyntheticPrivateNotificationsDoNotReleaseHistoricalRealInterestButRealPeerUnheartDoes()
    {
        var (first, second, linkService) = await SharedProfilesAsync();
        var original = await SeedFavoriteIntentAsync(owner, SharedFavoriteItem, jobId, realProviderIntent: true);
        await linkService.CreateAsync(FavoriteLinkTests.Fixture.Session(first, true), LinkRequest(first, second), default);
        var published = await SharedKept().PublishAsync(Request() with { ReferenceKey = $"heart:{original.StateId:N}" });
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).IsSynthetic = true;
            await db.SaveChangesAsync();
        }
        var pipeline = SharedPipeline();
        await pipeline.RecordAsync(SharedMutation(first, false, "test-unheart"));
        await using (var db = new AllstarrDbContext(database.Options))
            Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());

        await pipeline.RecordAsync(SharedMutation(second, false, "real-unheart"));

        await using var saved = new AllstarrDbContext(database.Options);
        Assert.Empty(await saved.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(0, (await saved.ManagedFiles.SingleAsync()).ReferenceCount);
        var cleanup = Assert.Single(await saved.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(other, cleanup.OwnerUserId);
        Assert.True(cleanup.AvailableAt >= DateTimeOffset.UtcNow.AddHours(23));
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Theory]
    [InlineData("unheart")]
    [InlineData("cycle")]
    [InlineData("synthetic")]
    public async Task FavoritePublicationRechecksSharedLifecycleAndCurrentSyntheticInsideItsWriteTransaction(string change)
    {
        var (first, second, linkService) = await SharedProfilesAsync();
        var original = await SeedFavoriteIntentAsync(owner, SharedFavoriteItem, jobId, realProviderIntent: true);
        await linkService.CreateAsync(FavoriteLinkTests.Fixture.Session(first, true), LinkRequest(first, second), default);
        await SharedPipeline().RecordAsync(SharedMutation(second, true, "same-value"));
        ArmBoundary(db =>
        {
            if (change == "synthetic") (db.Users.Single(item => item.Id == owner)).IsSynthetic = true;
            else
            {
                var shared = db.Set<LinkedProviderFavoriteStateRecord>().Single();
                shared.IsFavorite = change == "cycle";
                shared.LifecycleVersion += change == "cycle" ? 2 : 1;
                shared.Revision++;
            }
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SharedKept().PublishAsync(Request() with { ReferenceKey = $"heart:{original.StateId:N}" }));

        await using var saved = new AllstarrDbContext(database.Options);
        Assert.Null((await saved.Jobs.SingleAsync(item => item.Id == jobId)).CancellationRequestedAt);
        Assert.Empty(await saved.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.Empty(await saved.ManagedFileReferences.Where(item => item.ReferenceKey.StartsWith("heart:") && item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Fact]
    public async Task SameValuePeerHeartDoesNotInvalidateLegacyPublication()
    {
        var (first, second, linkService) = await SharedProfilesAsync();
        var original = await SeedFavoriteIntentAsync(owner, SharedFavoriteItem, jobId, realProviderIntent: true);
        await linkService.CreateAsync(FavoriteLinkTests.Fixture.Session(first, true), LinkRequest(first, second), default);
        await SharedPipeline().RecordAsync(SharedMutation(second, true, "peer-same"));
        var result = await SharedKept().PublishAsync(Request() with { ReferenceKey = $"heart:{original.StateId:N}" });
        Assert.Equal(audio, await File.ReadAllBytesAsync(result.Path));
        await using var saved = new AllstarrDbContext(database.Options);
        Assert.Equal(0, (await saved.Set<LinkedProviderFavoriteStateRecord>().SingleAsync()).LifecycleVersion);
        Assert.Null((await saved.Jobs.SingleAsync(item => item.Id == jobId)).CancellationRequestedAt);
    }

    [Fact]
    public async Task FirstLinkedSameRevisionUnheartMustCancelAndReleaseThePeersPrelinkHeart()
    {
        var (first, second, linkService) = await SharedProfilesAsync();
        var original = await SeedFavoriteIntentAsync(owner, SharedFavoriteItem, jobId, realProviderIntent: true);
        var published = await SharedKept().PublishAsync(Request() with { ReferenceKey = $"heart:{original.StateId:N}" });
        var pipeline = SharedPipeline();
        var privateUnheart = await pipeline.RecordAsync(SharedMutation(second, false, "unchanged"));
        var link = await linkService.CreateAsync(FavoriteLinkTests.Fixture.Session(first, true), LinkRequest(first, second), default);

        var boundUnheart = await pipeline.RecordAsync(SharedMutation(second, false, "unchanged"));

        Assert.True(boundUnheart.Created);
        Assert.NotEqual(privateUnheart.EventId, boundUnheart.EventId);
        await using (var saved = new AllstarrDbContext(database.Options))
        {
            var accepted = await saved.FavoriteEvents.SingleAsync(item => item.Id == boundUnheart.EventId);
            Assert.Equal(link.LinkId, accepted.SharedLinkId);
            Assert.Equal(0, accepted.SharedLifecycleVersion);
            Assert.NotNull((await saved.Jobs.SingleAsync(item => item.Id == original.JobId)).CancellationRequestedAt);
            Assert.Empty(await saved.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
            Assert.Equal(0, (await saved.ManagedFiles.SingleAsync()).ReferenceCount);
        }
        var claim = (await queue.ClaimNextAsync("union", [FavoriteLinkUnionJobHandler.Type]))!;
        var handler = new FavoriteLinkUnionJobHandler(factory, new SystemPlatformClock());
        var result = await handler.ExecuteAsync(new(claim, services), default);
        Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
        result = await handler.ExecuteAsync(new(claim, services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await queue.CompleteAsync(claim, result);
        Assert.Empty(await new FavoriteViewReader(factory).ReadAsync(FavoriteLinkTests.Fixture.Context(first)));
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    private RecordingKeptFiles SharedKept() => new(factory, artifacts, services.GetRequiredService<IServiceScopeFactory>(),
        placement, identities, new SystemPlatformClock(), jobs: queue);
    private FavoriteActionPipeline SharedPipeline() => new(factory, queue, new SystemPlatformClock(), SharedKept());
    private static FavoriteMutationRequest SharedMutation(UserRecord user, bool favorite, string revision) => new(
        FavoriteLinkTests.Fixture.Context(user), SharedFavoriteItem, favorite ? FavoriteOperation.Favorite : FavoriteOperation.Unfavorite, revision);
    private static CreateFavoriteLinkRequest LinkRequest(UserRecord first, UserRecord second) => new(Guid.NewGuid(), first.Id,
        FavoriteLinkScope.Fingerprint(first), second.Id, FavoriteLinkScope.Fingerprint(second), true, 1);
    private async Task<(UserRecord First, UserRecord Second, FavoriteLinkService Service)> SharedProfilesAsync()
    {
        await using var db = new AllstarrDbContext(database.Options);
        var first = await db.Users.SingleAsync(item => item.Id == owner);
        var second = await db.Users.SingleAsync(item => item.Id == other);
        first.IsAdmin = true;
        first.LastSeenAt = DateTimeOffset.UtcNow;
        second.BackendType = "subsonic";
        second.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return (first, second, new(factory, queue, new SystemPlatformClock()));
    }
}
