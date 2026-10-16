using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class FavoriteActionPipelineTests
{
    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("song")]
    [InlineData("album")]
    [InlineData("artist")]
    [InlineData("playlist")]
    public async Task DisplayBackfillRecoversLegacyFavoriteMetadataWithoutChangingTheHeart(string kind)
    {
        var itemId = $"ext-fixture-{kind}-legacy";
        var request = Request(FavoriteOperation.Favorite, "legacy") with { ItemId = itemId };
        await _pipeline.RecordAsync(request);
        var before = await new FavoriteViewReader(_factory).ReadAsync(request.ExecutionContext);
        Assert.StartsWith("Saved ", Assert.Single(before).Display.Title, StringComparison.Ordinal);
        var gateway = DisplayGateway(itemId);
        var pipeline = new FavoriteActionPipeline(_factory, _jobs, _clock, metadata: gateway.Object);
        var queue = new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock);

        Assert.Equal(1, await queue.EnqueuePendingAsync());
        var claim = await ClaimDisplayAsync();
        var completion = await new FavoriteDisplayBackfillJobHandler(_factory, pipeline, _clock)
            .ExecuteAsync(new(claim, new EmptyServices()), default);
        await _jobs.CompleteAsync(claim, completion);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        var after = Assert.Single(await new FavoriteViewReader(_factory).ReadAsync(request.ExecutionContext));
        Assert.Equal("Recovered title", after.Display.Title);
        Assert.Equal(itemId, after.ItemId);
        await using var db = _factory.CreateDbContext();
        var state = await db.Set<FavoriteStateRecord>().SingleAsync();
        Assert.True(state.IsFavorite);
        Assert.Equal(claim.Payload.Deserialize<FavoriteDisplayBackfillPayload>()!.EventId, state.LastEventId);
        Assert.DoesNotContain("untrusted", state.DisplayMetadataJson!, StringComparison.Ordinal);
        Assert.Equal(0, await queue.EnqueuePendingAsync());
    }

    [Fact]
    public async Task DisplayBackfillDiscoveryBoundsOutstandingJobsAndSkipsAlreadyAttemptedRows()
    {
        for (var index = 0; index < 105; index++)
            await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "legacy") with
            { ItemId = $"ext-fixture-song-{index}" });
        var queue = new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock);

        Assert.Equal(100, await queue.EnqueuePendingAsync());
        Assert.Equal(0, await queue.EnqueuePendingAsync());
        var claim = await ClaimDisplayAsync();
        await _jobs.CompleteAsync(claim, DurableJobCompletion.Failure("fixture_unavailable", "Unavailable"));
        Assert.Equal(1, await queue.EnqueuePendingAsync());

        await using var db = _factory.CreateDbContext();
        Assert.Equal(101, await db.Jobs.CountAsync(item => item.Type == FavoriteDisplayBackfillQueue.JobType));
        Assert.Equal(100, await db.Jobs.CountAsync(item => item.Type == FavoriteDisplayBackfillQueue.JobType &&
            item.State == DurableJobState.Pending));
        var next = await ClaimDisplayAsync();
        await _jobs.CompleteAsync(next, DurableJobCompletion.Failure("fixture_unavailable", "Unavailable"));
        _clock.UtcNow = _clock.UtcNow.AddDays(7);
        Assert.Equal(1, await queue.EnqueuePendingAsync());
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("unheart")]
    [InlineData("reheart")]
    [InlineData("disabled")]
    [InlineData("principal")]
    [InlineData("display")]
    [InlineData("cancelled")]
    [InlineData("attempt")]
    [InlineData("revision")]
    public async Task DisplayBackfillLookupHoldsNoWriterAndRejectsChangedAuthority(string change)
    {
        const string itemId = "ext-fixture-song-legacy";
        var request = Request(FavoriteOperation.Favorite, "legacy") with { ItemId = itemId };
        await _pipeline.RecordAsync(request);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "legacy"))
            .Returns(async (ProtocolExecutionContext execution, string _, string _) =>
            {
                entered.TrySetResult();
                await resume.Task.WaitAsync(execution.CancellationToken);
                return new Song { Id = itemId, Title = "Recovered title", Artist = "Recovered artist" };
            });
        var queue = new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock);
        Assert.Equal(1, await queue.EnqueuePendingAsync());
        var claim = await ClaimDisplayAsync();
        var handler = new FavoriteDisplayBackfillJobHandler(_factory,
            new(_factory, _jobs, _clock, metadata: gateway.Object), _clock);
        var running = handler.ExecuteAsync(new(claim, new EmptyServices()), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "independent") with
            { ItemId = "ext-fixture-song-independent" }).WaitAsync(TimeSpan.FromSeconds(30));
            if (change is "unheart" or "reheart")
            {
                await _pipeline.RecordAsync(request with { Operation = FavoriteOperation.Unfavorite, SourceRevision = "off" });
                if (change == "reheart") await _pipeline.RecordAsync(request with { SourceRevision = "on" });
            }
            else
            {
                await using var edit = _factory.CreateDbContext();
                var user = await edit.Users.SingleAsync(item => item.Id == _userId);
                var state = await edit.Set<FavoriteStateRecord>().SingleAsync(item => item.ItemId == itemId);
                var job = await edit.Jobs.SingleAsync(item => item.Id == claim.JobId);
                if (change == "disabled") user.Enabled = false;
                if (change == "principal") user.BackendPrincipalId = "changed-principal";
                if (change == "display") state.DisplayMetadataJson = JsonSerializer.Serialize(
                    new FavoriteDisplaySnapshot(1, itemId, "song", "Newer display", []), FavoriteDisplaySnapshot.JsonOptions);
                if (change == "cancelled") job.CancellationRequestedAt = _clock.UtcNow;
                if (change == "attempt") job.AttemptCount++;
                if (change == "revision") state.Revision++;
                await edit.SaveChangesAsync();
            }
        }
        finally { resume.TrySetResult(); }
        await running;

        await using var db = _factory.CreateDbContext();
        var saved = await db.Set<FavoriteStateRecord>().SingleAsync(item => item.ItemId == itemId);
        if (change == "revision") Assert.Equal("Recovered title", FavoriteDisplaySnapshot.Read(itemId, saved.DisplayMetadataJson)?.Title);
        else if (change == "display") Assert.Equal("Newer display", FavoriteDisplaySnapshot.Read(itemId, saved.DisplayMetadataJson)!.Title);
        else Assert.Null(saved.DisplayMetadataJson);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("wrong-id")]
    [InlineData("unsafe")]
    public async Task DisplayBackfillRetriesUnavailableOrInvalidMetadataWithoutHidingTheFavorite(string failure)
    {
        const string itemId = "ext-fixture-song-legacy";
        var request = Request(FavoriteOperation.Favorite, "legacy") with { ItemId = itemId };
        await _pipeline.RecordAsync(request);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "legacy"))
            .ReturnsAsync(failure == "unavailable" ? null : new Song
            { Id = failure == "wrong-id" ? "ext-fixture-song-other" : itemId, Title = "unsafe" == failure ? "Bad\nTitle" : "Title", Artist = "Artist" });
        Assert.Equal(1, await new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock).EnqueuePendingAsync());
        var claim = await ClaimDisplayAsync();

        var result = await new FavoriteDisplayBackfillJobHandler(_factory,
            new(_factory, _jobs, _clock, metadata: gateway.Object), _clock).ExecuteAsync(new(claim, new EmptyServices()), default);

        Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
        Assert.True(result.RetryDelay >= TimeSpan.FromMinutes(1));
        Assert.Equal("Saved track", Assert.Single(await new FavoriteViewReader(_factory).ReadAsync(request.ExecutionContext)).Display.Title);
    }

    [Fact]
    public async Task DisplayBackfillRateLimitsLookupsAcrossConcurrentJobs()
    {
        foreach (var id in new[] { "first", "second" })
            await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "legacy") with { ItemId = $"ext-fixture-song-{id}" });
        var clock = new ManualTimeProvider();
        var starts = new List<long>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext _, string _, string id) =>
            {
                starts.Add(clock.GetTimestamp());
                return Task.FromResult<Song?>(new() { Id = $"ext-fixture-song-{id}", Title = "Recovered title", Artist = "Artist" });
            });
        Assert.Equal(2, await new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock).EnqueuePendingAsync());
        var first = await ClaimDisplayAsync();
        var second = await ClaimDisplayAsync();
        using var handler = new FavoriteDisplayBackfillJobHandler(_factory, new(_factory, _jobs, _clock, metadata: gateway.Object), _clock,
            timeProvider: clock);

        var pending = Task.WhenAll(handler.ExecuteAsync(new(first, new EmptyServices()), default),
            handler.ExecuteAsync(new(second, new EmptyServices()), default));
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimerAsync());
        Assert.Single(starts);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Single(starts);
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, starts.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.GetElapsedTime(starts[0], starts[1]));
    }

    private async Task<DurableJobClaim> ClaimDisplayAsync() => Assert.IsType<DurableJobClaim>(
        await _jobs.ClaimNextAsync("display-backfill", [FavoriteDisplayBackfillQueue.JobType]));

    [Fact]
    public async Task DisplayBackfillCancellationPreservesTheMissingSnapshotForJobRecovery()
    {
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "legacy") with { ItemId = "ext-fixture-song-legacy" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "legacy"))
            .Returns(async (ProtocolExecutionContext execution, string _, string _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, execution.CancellationToken);
                return (Song?)null;
            });
        Assert.Equal(1, await new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock).EnqueuePendingAsync());
        var claim = await ClaimDisplayAsync();
        using var stop = new CancellationTokenSource();
        using var handler = new FavoriteDisplayBackfillJobHandler(_factory, new(_factory, _jobs, _clock, metadata: gateway.Object), _clock);
        var running = handler.ExecuteAsync(new(claim, new EmptyServices()), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, await _jobs.RecoverInterruptedAsync());
        Assert.Equal(claim.JobId, (await ClaimDisplayAsync()).JobId);
        await using var db = _factory.CreateDbContext();
        Assert.Null((await db.Set<FavoriteStateRecord>().SingleAsync()).DisplayMetadataJson);
    }

    [Fact]
    public async Task DisplayBackfillTimesOutAProviderThatIgnoresCancellation()
    {
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "legacy") with { ItemId = "ext-fixture-song-legacy" });
        var response = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "legacy")).Returns(response.Task);
        Assert.Equal(1, await new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock).EnqueuePendingAsync());
        var claim = await ClaimDisplayAsync();
        using var handler = new FavoriteDisplayBackfillJobHandler(_factory, new(_factory, _jobs, _clock, metadata: gateway.Object), _clock);
        try
        {
            var result = await handler.ExecuteAsync(new(claim, new EmptyServices()), default).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
            await using var db = _factory.CreateDbContext();
            Assert.Null((await db.Set<FavoriteStateRecord>().SingleAsync()).DisplayMetadataJson);
        }
        finally { response.TrySetResult(null); }
    }

    [Fact]
    public async Task DisplayBackfillDiscoverySkipsNativeReleasedDisabledAndCompleteFavorites()
    {
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "native") with { ItemId = "native-track" });
        var released = Request(FavoriteOperation.Favorite, "released") with { ItemId = "ext-fixture-song-released" };
        await _pipeline.RecordAsync(released);
        await _pipeline.RecordAsync(released with { Operation = FavoriteOperation.Unfavorite, SourceRevision = "off" });
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "disabled", _otherUserId) with
        { ItemId = "ext-fixture-song-disabled" });
        const string complete = "ext-fixture-song-complete";
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "complete") with { ItemId = complete });
        await using (var db = _factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == _otherUserId)).Enabled = false;
            (await db.Set<FavoriteStateRecord>().SingleAsync(item => item.ItemId == complete)).DisplayMetadataJson =
                JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, complete, "song", "Already saved", []), FavoriteDisplaySnapshot.JsonOptions);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock).EnqueuePendingAsync());
    }

    private static Mock<IProtocolProviderGateway> DisplayGateway(string itemId)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "legacy"))
            .ReturnsAsync(new Song { Id = itemId, Title = "Recovered title", Artist = "Recovered artist", Album = "Recovered album", LocalPath = "/untrusted/source.flac" });
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "legacy"))
            .ReturnsAsync(new Album { Id = itemId, Title = "Recovered title", Artist = "Recovered artist" });
        gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "legacy"))
            .ReturnsAsync(new Artist { Id = itemId, Name = "Recovered title" });
        gateway.Setup(item => item.GetPlaylistAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "legacy"))
            .ReturnsAsync(new allstarr.Models.Subsonic.ExternalPlaylist { Id = itemId, Name = "Recovered title" });
        return gateway;
    }

    [Fact]
    public async Task DisplayBackfillPreservesLegacyIdsWhenProviderMetadataUsesTheTypedId()
    {
        const string itemId = "ext-fixture-legacy";
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "legacy") with { ItemId = itemId });
        var gateway = DisplayGateway("ext-fixture-song-legacy");
        Assert.Equal(1, await new FavoriteDisplayBackfillQueue(_factory, _jobs, _clock).EnqueuePendingAsync());
        var claim = await ClaimDisplayAsync();
        using var handler = new FavoriteDisplayBackfillJobHandler(_factory,
            new(_factory, _jobs, _clock, metadata: gateway.Object), _clock);

        var completion = await handler.ExecuteAsync(new(claim, new EmptyServices()), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await using var db = _factory.CreateDbContext();
        var state = await db.Set<FavoriteStateRecord>().SingleAsync();
        Assert.Equal(itemId, state.ItemId);
        Assert.Equal("Recovered title", FavoriteDisplaySnapshot.Read(itemId, state.DisplayMetadataJson)?.Title);
    }
}
