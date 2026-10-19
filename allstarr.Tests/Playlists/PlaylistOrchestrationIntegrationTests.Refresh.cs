using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task Refresh_publishes_pending_rows_and_reuses_the_active_background_job()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("pending", Entry(0, "pending", "new-source", "New Song"));
        var gateway = MatchingGateway();
        var (service, matches) = BackgroundMatching(gateway);
        using var disconnected = new CancellationTokenSource();

        var first = await service.RefreshAsync(Context(), _link, cancellationToken: disconnected.Token);
        var repeated = await service.RefreshAsync(Context(), _link);
        disconnected.Cancel();

        gateway.Verify(item => item.SearchPlayableSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        Assert.NotNull(first.JobId);
        Assert.Equal(first.JobId, repeated.JobId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = Assert.Single(await db.Jobs.ToListAsync());
            Assert.Equal(TrackRematchJobHandler.Type, job.Type);
            Assert.Equal(DurableJobState.Pending, job.State);
            Assert.NotNull((await db.PlaylistSourceSnapshots.SingleAsync()).PublishedAt);
        }

        var queue = RematchQueue();
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        var completion = await ExecuteRefreshRematch(service, matches, claim);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await queue.CompleteAsync(claim, completion);

        var projection = await new DurablePlaylistProjectionReader(_factory,
            new TestBackendLibraryAccess(_factory, "music"), gateway.Object).ReadByLinkIdAsync(_user, _link);
        Assert.NotNull(projection);
        Assert.Equal(1, projection.ExternalCount);
        Assert.Equal(1, projection.PlayableCount);
        await using var verify = await _factory.CreateDbContextAsync();
        var latest = await verify.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(TrackMatchState.Accepted, latest.State);
        Assert.Equal(latest.Id, (await verify.PlaylistSourceEntries.SingleAsync()).PublishedTrackMatchId);
    }

    [Fact]
    public async Task Refresh_job_reads_the_latest_snapshot_and_allows_a_later_job_after_completion()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        var gateway = MatchingGateway();
        var (service, matches) = BackgroundMatching(gateway);
        _source.Snapshot = Snapshot("first", Entry(0, "old", "old-source", "Old Song"));
        var first = await service.RefreshAsync(Context(), _link);
        _source.Snapshot = Snapshot("second", Entry(0, "new", "new-source", "New Song"));
        var second = await service.RefreshAsync(Context(), _link);
        Assert.Equal(first.JobId, second.JobId);

        var queue = RematchQueue();
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        var completion = await ExecuteRefreshRematch(service, matches, claim);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await queue.CompleteAsync(claim, completion);
        gateway.Verify(item => item.SearchPlayableSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), It.Is<string>(query => query.Contains("Old Song")), It.IsAny<int>()), Times.Never);
        var projection = await new DurablePlaylistProjectionReader(_factory,
            new TestBackendLibraryAccess(_factory, "music"), gateway.Object).ReadByLinkIdAsync(_user, _link);
        Assert.Equal(second.SnapshotId, projection!.SnapshotId);
        Assert.Equal(1, projection.ExternalCount);

        _source.Snapshot = Snapshot("third", Entry(0, "later", "later-source", "Later Song"));
        var third = await service.RefreshAsync(Context(), _link);
        Assert.NotNull(third.JobId);
        Assert.NotEqual(first.JobId, third.JobId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_arriving_before_job_completion_keeps_the_same_job_ready_to_resume(bool failedAttempt)
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        var gateway = MatchingGateway();
        var (service, matches) = BackgroundMatching(gateway);
        _source.Snapshot = Snapshot("first", Entry(0, "first", "new-source", "New Song"));
        var first = await service.RefreshAsync(Context(), _link);
        var queue = RematchQueue();
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        var completion = await ExecuteRefreshRematch(service, matches, claim);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);

        _source.Snapshot = Snapshot("second", Entry(0, "second", "other-source", "New Song"));
        var second = await service.RefreshAsync(Context(), _link);
        Assert.Equal(first.JobId, second.JobId);
        await queue.CompleteAsync(claim, failedAttempt
            ? DurableJobCompletion.Failure("old_snapshot_failed", "The previous snapshot could not finish.")
            : completion);

        var resumed = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        Assert.Equal(claim.JobId, resumed.JobId);
        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await ExecuteRefreshRematch(service, matches, resumed)).Kind);
        var projection = await new DurablePlaylistProjectionReader(_factory,
            new TestBackendLibraryAccess(_factory, "music"), gateway.Object).ReadByLinkIdAsync(_user, _link);
        Assert.Equal(second.SnapshotId, projection!.SnapshotId);
        Assert.Equal(1, projection.ExternalCount);
    }

    [Fact]
    public async Task Refresh_reuses_an_already_queued_scoped_playlist_rematch()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("pending", Entry(0, "pending", "new-source", "New Song"));
        await _service.RefreshAsync(Context(), _link);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.TrackMatches.SingleAsync()).MatcherVersion = "retired";
            await db.SaveChangesAsync();
        }
        var rematches = new PlaylistRematchService(_factory,
            new DurablePlaylistProjectionReader(_factory, new TestBackendLibraryAccess(_factory, "music")),
            new TrackMatchDecisionEngine(), new TestBackendLibraryAccess(_factory, "music"));
        var preview = await rematches.PreviewAsync(_user, playlistLinkId: _link);
        var queue = RematchQueue();
        var manual = await queue.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistRematchJobPayload>(
            TrackRematchJobHandler.Type, "scoped-playlist-rematch",
            new(preview.ConfirmationId, preview.ScopeFingerprint, preview.Targets, _link), _user));
        var (service, matches) = BackgroundMatching(MatchingGateway());

        var refresh = await service.RefreshAsync(Context(), _link);

        Assert.Equal(manual.JobId, refresh.JobId);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Single(await verify.Jobs.ToListAsync());
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await ExecuteRefreshRematch(service, matches, claim)).Kind);
    }

    [Fact]
    public async Task Refresh_does_not_attach_new_work_to_a_cancelling_job()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        var (service, _) = BackgroundMatching(MatchingGateway());
        _source.Snapshot = Snapshot("first", Entry(0, "first", "new-source", "New Song"));
        var first = await service.RefreshAsync(Context(), _link);
        var queue = RematchQueue();
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        Assert.True(await queue.RequestCancellationAsync(claim.JobId, _user));

        var second = await service.RefreshAsync(Context(), _link);

        Assert.NotNull(second.JobId);
        Assert.NotEqual(first.JobId, second.JobId);
        await queue.CompleteAsync(claim, DurableJobCompletion.Cancelled());
        Assert.Equal(second.JobId, (await queue.ClaimNextAsync("refresh-worker"))!.JobId);
    }

    [Fact]
    public async Task Refresh_job_limits_simultaneous_row_matching_to_three()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("concurrent-pending", Enumerable.Range(0, 4)
            .Select(index => Entry(index, $"entry-{index}", $"source-{index + 10}", $"Missing {index}")).ToArray());
        await _service.RefreshAsync(Context(), _link);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var repository = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        repository.Setup(item => item.RematchSnapshotAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, Guid _, string _, string _, CancellationToken token) =>
            {
                var simultaneous = Interlocked.Increment(ref active);
                Assert.InRange(simultaneous, 1, 3);
                if (Interlocked.Increment(ref calls) == 3) entered.TrySetResult();
                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
                return new TrackRematchCommandResult(true);
            });
        var claim = new DurableJobClaim(Guid.CreateVersion7(), 1, TrackRematchJobHandler.Type,
            JsonSerializer.SerializeToElement(new PlaylistRematchJobPayload("", "", PlaylistLinkId: _link, RefreshPending: true)),
            _user, null, null, JsonSerializer.SerializeToElement(new { }), "concurrent-refresh");

        var running = ExecuteRefreshRematch(_service, repository.Object, claim);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted);
            Assert.Equal(3, Volatile.Read(ref calls));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await running).Kind);
        Assert.Equal(4, calls);
    }

    private static Mock<IProtocolProviderGateway> MatchingGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync([new Song { ExternalProvider = "deezer", ExternalId = "new-song", Title = "New Song",
                Artist = "Artist", Album = "Album", Duration = 180 }]);
        return gateway;
    }

    private (PlaylistOrchestrationService Service, TrackMatchCommandService Matches) BackgroundMatching(
        Mock<IProtocolProviderGateway> gateway)
    {
        var matcher = new TrackMatchDecisionEngine();
        var matches = new TrackMatchCommandService(_factory, matcher, new ProviderAccountResolver(_factory),
            new Clock(_now), new TestBackendLibraryAccess(_factory, "music"),
            new TrackMatchPlayableSearch(gateway.Object, matcher, NullLogger<TrackMatchPlayableSearch>.Instance));
        var service = new PlaylistOrchestrationService(_factory, _source, new FakeTargetResolver(_target),
            new PlaylistMaterializationPlanner(), matcher, matches, new Clock(_now), new KeyedAsyncLock(),
            new TestBackendLibraryAccess(_factory, "music"), providerGateway: gateway.Object, jobs: RematchQueue());
        return (service, matches);
    }

    private DurableJobQueue RematchQueue()
    {
        var options = new DurableJobOptions();
        return new(_factory, options, new JobPayloadPolicy(options), new Clock(_now));
    }

    private Task<DurableJobCompletion> ExecuteRefreshRematch(
        PlaylistOrchestrationService service, ITrackMatchRepository matches, DurableJobClaim claim) =>
        new PlaylistRematchJobHandler(_factory,
                new PlaylistRematchService(_factory,
                    new DurablePlaylistProjectionReader(_factory, new TestBackendLibraryAccess(_factory, "music")),
                    new TrackMatchDecisionEngine(), new TestBackendLibraryAccess(_factory, "music")),
                matches, service, new Clock(_now))
            .ExecuteAsync(new DurableJobExecutionContext(claim, EmptyServices.Instance), CancellationToken.None);
}
