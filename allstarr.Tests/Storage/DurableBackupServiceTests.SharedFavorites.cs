using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class DurableBackupServiceTests
{
    [Fact]
    public async Task SharedHeartsBindingsTombstoneAndUnionCheckpointSurviveDatabaseAndKeyRingRestore()
    {
        UserRecord first;
        UserRecord second;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            first = await db.Users.SingleAsync(item => item.Id == _userId);
            first.IsAdmin = true;
            second = FavoriteLinkTests.Fixture.Copy(first, Guid.NewGuid(), "other-dialect", "subsonic", false);
            db.Users.Add(second);
            await db.SaveChangesAsync();
        }
        var clock = new SystemPlatformClock();
        var queue = new DurableJobQueue(_factory, new(), new JobPayloadPolicy(new()), clock);
        var pipeline = new FavoriteActionPipeline(_factory, queue, clock);
        await pipeline.RecordAsync(new(FavoriteLinkTests.Fixture.Context(first), "ext-deezer-song-42", FavoriteOperation.Favorite, "first"));
        var links = new FavoriteLinkService(_factory, queue, clock);
        var request = new CreateFavoriteLinkRequest(Guid.NewGuid(), first.Id, FavoriteLinkScope.Fingerprint(first), second.Id,
            FavoriteLinkScope.Fingerprint(second), true, 1);
        var link = await links.CreateAsync(FavoriteLinkTests.Fixture.Session(first, true), request, default);
        var falseIntent = await pipeline.RecordAsync(new(FavoriteLinkTests.Fixture.Context(second), "ext-deezer-song-42", FavoriteOperation.Unfavorite, "second"));
        var claim = (await queue.ClaimNextAsync("union-checkpoint", [FavoriteLinkUnionJobHandler.Type]))!;
        var batch = await new FavoriteLinkUnionJobHandler(_factory, clock).ExecuteAsync(new(claim, new EmptyFavoriteServices()), default);
        Assert.Equal(DurableJobCompletionKind.Deferred, batch.Kind);
        await queue.CompleteAsync(claim, batch);
        var service = Service();
        var backup = await service.CreateAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Set<LinkedProviderFavoriteStateRecord>().ExecuteUpdateAsync(update => update.SetProperty(item => item.IsFavorite, true).SetProperty(item => item.LifecycleVersion, 99));
            await db.Set<ProviderFavoriteLinkRecord>().ExecuteUpdateAsync(update => update.SetProperty(item => item.ProcessedRows, 99));
        }

        await StageAsync(service, backup);
        await RestartAsync();

        Assert.Equal(DurableStorageReadiness.Ready, _state.GetSnapshot().Readiness);
        await using var restored = await _factory.CreateDbContextAsync();
        var header = await restored.Set<ProviderFavoriteLinkRecord>().SingleAsync();
        Assert.Equal(link.LinkId, header.Id);
        Assert.Equal(link.JobId, header.UnionJobId);
        Assert.Equal(1, header.ProcessedRows);
        Assert.Equal(1, header.Phase);
        Assert.Equal(0, header.SeededItems);
        Assert.Null(header.CompletedAt);
        Assert.Equal(2, await restored.Set<ProviderFavoriteLinkMemberRecord>().CountAsync());
        Assert.Equal(FavoriteLinkScope.Fingerprint(second), (await restored.Set<ProviderFavoriteLinkMemberRecord>().SingleAsync(item => item.UserId == second.Id)).IdentityFingerprint);
        var tombstone = await restored.Set<LinkedProviderFavoriteStateRecord>().SingleAsync();
        Assert.False(tombstone.IsFavorite);
        Assert.Equal(0, tombstone.LifecycleVersion);
        Assert.Equal(falseIntent.EventId, tombstone.LastIntentEventId);
        var receipt = await restored.Set<ProviderFavoriteLinkRequestRecord>().SingleAsync();
        Assert.Equal(request.RequestId, receipt.RequestId);
        Assert.Equal(link.LinkId, receipt.LinkId);
        Assert.True((await restored.FavoriteEvents.SingleAsync(item => item.Id == falseIntent.EventId)).IsRealProviderIntent);
        Assert.Equal(_secretId, (await restored.SecretReferences.SingleAsync()).Id);
        Assert.Equal(0, await restored.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    private sealed class EmptyFavoriteServices : IServiceProvider
    { public object? GetService(Type serviceType) => null; }
}
