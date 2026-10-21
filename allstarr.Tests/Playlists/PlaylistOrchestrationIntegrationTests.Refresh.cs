using System.Text.Json;
using System.Collections.Immutable;
using allstarr.Core.Settings;
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
    [Theory]
    [InlineData(ProviderUseMode.ManualOnly, false)]
    [InlineData(ProviderUseMode.Off, false)]
    [InlineData(ProviderUseMode.ManualOnly, true)]
    [InlineData(ProviderUseMode.Off, true)]
    public async Task RefreshDoesNotSelectRestrictedSourcesOrReuseTheirAutomaticMatches(ProviderUseMode mode, bool previouslyOn)
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("stable-source", Entry(0, "one", "source-1", "One"));
        var currentMode = previouslyOn ? ProviderUseMode.Automatic : mode;
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(_user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "High", 0.07)
            { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("fixture", currentMode) });
        var service = new PlaylistOrchestrationService(_factory, _source, new FakeTargetResolver(_target),
            new PlaylistMaterializationPlanner(), new TrackMatchDecisionEngine(), _trackMatches,
            new Clock(_now), new KeyedAsyncLock(), new TestBackendLibraryAccess(_factory, "music"),
            effectivePolicies: policies.Object);
        if (previouslyOn)
        {
            await service.RefreshAsync(Context(), _link);
            await using var before = await _factory.CreateDbContextAsync();
            Assert.Equal(TrackMatchState.Accepted, (await before.TrackMatches.SingleAsync()).State);
            currentMode = mode;
        }
        await service.RefreshAsync(Context(), _link);
        await using var db = await _factory.CreateDbContextAsync();
        var latest = await db.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(TrackMatchState.Unresolved, latest.State);
        Assert.Null(latest.LibraryTrackId);
        Assert.Null(latest.CanonicalRecordingId);
    }

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

        gateway.Verify(item => item.SearchAutomaticSongsAsync(
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
        gateway.Verify(item => item.SearchAutomaticSongsAsync(
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unchanged_refresh_keeps_accepted_decisions_and_identities(bool local)
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = local
            ? Snapshot("stable", Entry(0, "one", "source-1", "One"))
            : Snapshot("stable", Entry(0, "pending", "new-source", "New Song"));
        var (service, matches) = BackgroundMatching(MatchingGateway());
        var queue = RematchQueue();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            await service.RefreshAsync(Context(), _link);
            var queued = await queue.ClaimNextAsync("refresh-worker");
            var claim = queued ?? new DurableJobClaim(Guid.CreateVersion7(), 1, TrackRematchJobHandler.Type,
                JsonSerializer.SerializeToElement(new PlaylistRematchJobPayload("", "", PlaylistLinkId: _link, RefreshPending: true)),
                _user, null, null, JsonSerializer.SerializeToElement(new { }), "repeat-refresh");
            var completion = await ExecuteRefreshRematch(service, matches, claim);
            Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
            if (queued != null) await queue.CompleteAsync(claim, completion);
            await using var db = await _factory.CreateDbContextAsync();
            var decisions = await db.TrackMatches.ToArrayAsync();
            Assert.Equal(local ? 1 : 2, decisions.Length);
            Assert.Equal(local ? 1 : 2, decisions.Max(item => item.DecisionVersion));
            Assert.Equal(TrackMatchState.Accepted, decisions.MaxBy(item => item.DecisionVersion)!.State);
            Assert.Equal(local ? 2 : 4, await db.ProviderTrackIdentities.CountAsync());
        }
    }

    [Fact]
    public async Task Pending_playlist_rows_do_not_cause_accepted_neighbors_to_be_redecided()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("mixed", Entry(0, "accepted", "new-source", "New Song"),
            Entry(1, "pending", "missing-source", "Unrelated composition"));
        var (service, matches) = BackgroundMatching(MatchingGateway());
        var queue = RematchQueue();
        Guid? acceptedSnapshotId = null;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var refresh = await service.RefreshAsync(Context(), _link);
            Assert.NotNull(refresh.JobId);
            var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
            var completion = await ExecuteRefreshRematch(service, matches, claim);
            Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
            await queue.CompleteAsync(claim, completion);
            await using var db = await _factory.CreateDbContextAsync();
            acceptedSnapshotId ??= (await db.PlaylistSourceEntries.SingleAsync(item => item.SourcePosition == 0)).ExternalMetadataSnapshotId;
            var decisions = await db.TrackMatches.Where(item => item.ExternalSnapshotId == acceptedSnapshotId).ToArrayAsync();
            Assert.Equal(2, decisions.Length);
            Assert.Equal(TrackMatchState.Accepted, decisions.MaxBy(item => item.DecisionVersion)!.State);
            Assert.Equal(4, await db.ProviderTrackIdentities.CountAsync());
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("library")]
    [InlineData("matcher")]
    [InlineData("policy")]
    public async Task Refresh_redecides_when_matching_inputs_change(string change)
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("stable", Entry(0, "pending", "new-source", "New Song"));
        var (service, matches) = BackgroundMatching(MatchingGateway());
        var queue = RematchQueue();
        await service.RefreshAsync(Context(), _link);
        var first = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        var completion = await ExecuteRefreshRematch(service, matches, first);
        await queue.CompleteAsync(first, completion);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            if (change == "library") (await db.LibraryTracks.FirstAsync()).Title = "Changed title";
            if (change == "matcher") (await db.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync()).MatcherVersion = "retired";
            if (change == "policy") (await db.PlaylistLinks.SingleAsync()).PolicyVersion = "updated-policy";
            await db.SaveChangesAsync();
        }
        if (change == "source")
            _source.Snapshot = Snapshot("changed", Entry(0, "pending", "new-source", "New Song") with { Album = "Changed album" });
        await service.RefreshAsync(Context(), _link);
        var next = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await ExecuteRefreshRematch(service, matches, next)).Kind);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.True(await verify.TrackMatches.CountAsync() > 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_accepted_rows_only_need_publication_repair(bool generic)
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("stable", Entry(0, "pending", "new-source", "New Song"));
        var (service, matches) = BackgroundMatching(MatchingGateway());
        var queue = RematchQueue();
        await service.RefreshAsync(Context(), _link);
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        await queue.CompleteAsync(claim, await ExecuteRefreshRematch(service, matches, claim));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            if (generic) (await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "deezer")).ProviderId = "external";
            await db.SaveChangesAsync();
        }
        var rematches = new PlaylistRematchService(_factory,
            new DurablePlaylistProjectionReader(_factory, new TestBackendLibraryAccess(_factory, "music")),
            new TrackMatchDecisionEngine(), new TestBackendLibraryAccess(_factory, "music"));
        var current = await rematches.PreviewAsync(_user, playlistLinkId: _link, includePending: true);
        Assert.Equal(0, current.StaleRevisionRows);
        Assert.Equal(generic ? 1 : 0, current.GenericExternalRows);
        Assert.Empty(current.Targets);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PlaylistSourceEntries.SingleAsync()).PublishedTrackMatchId = null;
            await db.SaveChangesAsync();
        }
        var repair = await rematches.PreviewAsync(_user, playlistLinkId: _link, includePending: true);
        Assert.False(Assert.Single(repair.Targets).RequiresDecision);
        if (generic)
            Assert.True(Assert.Single((await rematches.PreviewAsync(_user, playlistLinkId: _link)).Targets).RequiresDecision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rematch_retains_an_existing_verified_route_when_no_better_evidence_is_available(bool lookupFails)
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("stable", Entry(0, "pending", "new-source", "New Song"));
        var gateway = MatchingGateway();
        var (service, matches) = BackgroundMatching(gateway);
        var queue = RematchQueue();
        await service.RefreshAsync(Context(), _link);
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        await queue.CompleteAsync(claim, await ExecuteRefreshRematch(service, matches, claim));
        if (lookupFails)
            gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "new-song"))
                .ThrowsAsync(new HttpRequestException("Temporarily unavailable"));
        else
            gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "new-song"))
                .ReturnsAsync(new Song
                {
                    ExternalProvider = "deezer",
                    ExternalId = "new-song",
                    Title = "New Song",
                    Artist = "Artist",
                    Album = "Album",
                    Duration = 180
                });
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync([new Song { ExternalProvider = "deezer", ExternalId = "equivalent", Title = "New Song",
                Artist = "Artist", Album = "Album", Duration = 180 }]);
        await using var db = await _factory.CreateDbContextAsync();
        var snapshot = await db.ExternalMetadataSnapshots.SingleAsync();
        Assert.True((await matches.RematchSnapshotAsync(Context(), snapshot.Id, "retain", "updated-policy")).Succeeded);
        var retained = Assert.Single(await db.ProviderTrackIdentities.Where(item => item.ProviderId == "deezer").ToArrayAsync());
        Assert.Equal("new-song", retained.ExternalId);
        Assert.Equal("automatic-match", retained.VerificationMethod);
    }

    [Theory]
    [InlineData("automatic-match", false, false)]
    [InlineData("automatic-suggestion", false, false)]
    [InlineData("automatic-match", true, false)]
    [InlineData("automatic-match", false, true)]
    public async Task Rematch_keeps_one_active_identity_per_provider(string method, bool pinned, bool mismatchedMetadata)
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("stable", Entry(0, "pending", "new-source", "New Song"));
        var gateway = MatchingGateway();
        var (service, matches) = BackgroundMatching(gateway);
        var queue = RematchQueue();
        await service.RefreshAsync(Context(), _link);
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("refresh-worker"));
        await queue.CompleteAsync(claim, await ExecuteRefreshRematch(service, matches, claim));
        Guid snapshotId;
        Guid previousId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            snapshotId = (await db.ExternalMetadataSnapshots.SingleAsync()).Id;
            var previous = await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "deezer");
            previousId = previous.Id;
            previous.VerificationMethod = method;
            if (pinned) previous.Verification = ProviderIdentityVerification.Pinned;
            await db.SaveChangesAsync();
        }
        if (mismatchedMetadata)
            gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "new-song"))
                .ReturnsAsync(new Song
                {
                    ExternalProvider = "deezer",
                    ExternalId = "new-song",
                    Title = "Unrelated composition",
                    Artist = "Different performer",
                    Album = "Other collection",
                    Duration = 300
                });
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync([new Song { ExternalProvider = "deezer", ExternalId = "replacement", Title = "New Song",
                Artist = "Artist", Album = "Album", Duration = 180 }]);
        Assert.True((await matches.RematchSnapshotAsync(Context(), snapshotId, "replace", "updated-policy")).Succeeded);
        await using var verify = await _factory.CreateDbContextAsync();
        var identities = await verify.ProviderTrackIdentities.Where(item => item.ProviderId == "deezer").ToArrayAsync();
        var active = Assert.Single(identities, item => item.VerificationMethod != "superseded");
        Assert.Equal(pinned ? "new-song" : "replacement", active.ExternalId);
        if (pinned)
        {
            Assert.Equal(previousId, active.Id);
            Assert.Equal(method, active.VerificationMethod);
            Assert.Equal(ProviderIdentityVerification.Pinned, active.Verification);
            Assert.Single(identities);
        }
        else Assert.Equal("superseded", identities.Single(item => item.Id == previousId).VerificationMethod);
    }

    private static Mock<IProtocolProviderGateway> MatchingGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticSongsAsync(
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
