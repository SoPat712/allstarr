using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class FavoriteActionPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticActivity_HeartsDoNotCreateOrExecuteAutomaticDownloads(bool designateAfterAcceptance)
    {
        if (!designateAfterAcceptance)
        {
            await using var before = _factory.CreateDbContext();
            (await before.Users.SingleAsync(item => item.Id == _userId)).IsSynthetic = true;
            await before.SaveChangesAsync();
        }
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "synthetic-heart") with { ItemId = "ext-deezer-song-42" });
        await using (var db = _factory.CreateDbContext())
        {
            if (!designateAfterAcceptance)
                Assert.DoesNotContain(await db.FavoriteActions.ToArrayAsync(), action => action.ActionType == "download");
            (await db.Users.SingleAsync(item => item.Id == _userId)).IsSynthetic = true;
            await db.SaveChangesAsync();
        }
        var downloads = new List<string>();
        var handler = new FavoriteActionJobHandler(_factory, [new RecordingExecutor("download", downloads)], _clock);
        var claim = await _jobs.ClaimNextAsync("synthetic-heart", [FavoriteActionPipeline.JobType]);
        var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Empty(downloads);
        await using var verify = _factory.CreateDbContext();
        Assert.True((await verify.FavoriteStates.SingleAsync()).IsFavorite);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyntheticActivity_FavoritesRemainFunctionalWithoutRecommendationSignals(bool synthetic)
    {
        await using (var db = _factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == _userId)).IsSynthetic = synthetic;
            await db.SaveChangesAsync();
        }
        var writes = 0;
        var signals = new Mock<IRecommendationSignalWriter>();
        signals.Setup(item => item.WriteAsync(It.IsAny<IntelligenceScope>(), "favorite", It.IsAny<string>(),
                It.IsAny<double>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback(() => writes++).ReturnsAsync(true);
        var downloads = new List<string>();
        var handler = new FavoriteActionJobHandler(_factory, [new RecordingExecutor("download", downloads)], _clock, signals.Object);
        foreach (var operation in new[] { FavoriteOperation.Favorite, FavoriteOperation.Unfavorite })
        {
            var accepted = await _pipeline.RecordAsync(Request(operation, operation.ToString()) with
            {
                ItemId = "ext-fixture-song-track-1"
            });
            var claim = await _jobs.ClaimNextAsync("exclusion", [FavoriteActionPipeline.JobType]);
            Assert.NotNull(claim);
            var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await _jobs.CompleteAsync(claim!, result);
            Assert.True(accepted.Created);
            await using var db = _factory.CreateDbContext();
            Assert.Equal(operation == FavoriteOperation.Favorite, (await db.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
        }
        Assert.Equal(synthetic ? 0 : 2, writes);
        Assert.Equal(synthetic ? 0 : 1, downloads.Count);
    }
}
