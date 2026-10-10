using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed class TrackRematchJobHandlerTests
{
    [Fact]
    public async Task SongScope_RematchesTheOwnersSnapshot()
    {
        var owner = Guid.CreateVersion7();
        var snapshot = Guid.CreateVersion7();
        var repository = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        repository.Setup(item => item.RematchSnapshotAsync(
                It.Is<TrackMatchActor>(actor => actor.UserId == owner && !actor.IsAdministrator),
                snapshot,
                "correlation",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackRematchCommandResult(true));
        var handler = new TrackRematchJobHandler(null!, null!, repository.Object);

        var completion = await handler.ExecuteAsync(
            Context(new TrackRematchSongPayload(snapshot), owner), CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        repository.VerifyAll();
    }

    [Fact]
    public async Task SongScope_MissingSnapshotFailsWithoutRetry()
    {
        var repository = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        var handler = new TrackRematchJobHandler(null!, null!, repository.Object);

        var completion = await handler.ExecuteAsync(
            Context(new TrackRematchSongPayload(Guid.Empty), Guid.CreateVersion7()), CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
    }

    [Fact]
    public async Task UnknownScope_FailsWithoutRetry()
    {
        var handler = new TrackRematchJobHandler(null!, null!, new Mock<ITrackMatchRepository>(MockBehavior.Strict).Object);

        var completion = await handler.ExecuteAsync(
            Context(new { Scope = "everything" }, Guid.CreateVersion7()), CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
    }

    [Fact]
    public void Registration_UsesOneRematchJobHandler()
    {
        var handlers = new ServiceCollection().AddTrackIdentity()
            .Where(item => item.ServiceType == typeof(IDurableJobHandler))
            .Select(item => item.ImplementationType)
            .ToArray();

        Assert.Contains(typeof(TrackRematchJobHandler), handlers);
        Assert.DoesNotContain(typeof(PlaylistRematchJobHandler), handlers);
        Assert.DoesNotContain(typeof(TrackRematchAllJobHandler), handlers);
        Assert.False(typeof(IDurableJobHandler).IsAssignableFrom(typeof(PlaylistRematchJobHandler)));
        Assert.False(typeof(IDurableJobHandler).IsAssignableFrom(typeof(TrackRematchAllJobHandler)));
        Assert.Equal("track-match.rematch", TrackRematchJobHandler.Type);
    }

    private static DurableJobExecutionContext Context(object payload, Guid owner) => new(
        new DurableJobClaim(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            TrackRematchJobHandler.Type,
            JsonSerializer.SerializeToElement(payload),
            owner,
            null,
            null,
            JsonSerializer.SerializeToElement(new { }),
            "correlation",
            "worker",
            DateTimeOffset.UtcNow.AddMinutes(1)),
        new ServiceCollection().BuildServiceProvider());
}
