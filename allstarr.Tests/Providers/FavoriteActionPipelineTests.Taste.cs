using allstarr.Core.Enrichment;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using Moq;

namespace allstarr.Tests;

public sealed partial class FavoriteActionPipelineTests
{
    [Fact]
    public async Task Taste_HeartsTeachOnceAndImportedLikesDoNotTeach()
    {
        var recording = Guid.NewGuid();
        var tags = new SharedTagStore(_factory, _clock);
        await tags.SaveAsync(SharedTagStore.RecordingKey(recording), [new("provider", [new("pop")], _clock.UtcNow)], [], false, default);
        var taste = new TasteService(_factory, tags, _clock);
        var resolver = new Mock<IPlaybackTrackResolver>();
        resolver.Setup(item => item.ResolveAsync(It.IsAny<PlaybackSignalPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaybackTrackSnapshot(null, recording, "native-track", "Track", "Singer", null, 120000));
        var handler = new FavoriteActionJobHandler(_factory, [], _clock, taste: taste, tasteTracks: resolver.Object);
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "heart") with { ItemId = "native-track" });
        var claim = await _jobs.ClaimNextAsync("heart", [FavoriteActionPipeline.JobType]);
        var execution = new DurableJobExecutionContext(claim!, EmptyServices.Instance);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(execution, default)).Kind);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(execution, default)).Kind);
        Assert.Equal(2, Assert.Single((await taste.GetAsync(_userId)).Usually.Tags).Value);
        await _jobs.CompleteAsync(claim!, DurableJobCompletion.Success());
        await _pipeline.ImportSpotifyLikeAsync(Request(FavoriteOperation.Favorite, "import").ExecutionContext, "native-import", "import", default);
        claim = await _jobs.ClaimNextAsync("import", [FavoriteActionPipeline.JobType]);
        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        Assert.Equal(2, Assert.Single((await taste.GetAsync(_userId)).Usually.Tags).Value);
    }
}
