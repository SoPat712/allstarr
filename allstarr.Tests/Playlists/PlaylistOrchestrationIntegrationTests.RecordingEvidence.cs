using allstarr.Core.Storage;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task PlaylistLibraryLinkUsesNewMetadataForAnUnchangedIndexedSongAndSkipsReusedSnapshots()
    {
        LibraryTrackRecord original;
        await using (var setup = _factory.CreateDbContext())
        {
            original = await setup.LibraryTracks.AsNoTracking().SingleAsync(item => item.Id == _trackOne);
            Assert.Null(original.CanonicalRecordingId);
            foreach (var identity in await setup.ProviderTrackIdentities.ToArrayAsync())
            {
                identity.Scope = ProviderIdentityScope.Account;
                identity.ProviderAccountId = _account;
            }
            await setup.SaveChangesAsync();
        }
        var clock = new Clock(_now);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var identities = new TrackIdentityService(_factory, state, clock);
        Assert.Equal(0, (await identities.LinkLibraryRecordingsAsync(Context().RequireActor(), "before", _canonical)).Linked);
        var links = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        links.Setup(item => item.LinkLibraryRecordingsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), null, It.IsAny<CancellationToken>()))
            .Returns((ProviderActorContext actor, string correlation, Guid? recording, Guid? track, CancellationToken token) =>
                identities.LinkLibraryRecordingsAsync(actor, correlation, recording, track, token));
        var service = new PlaylistOrchestrationService(_factory, _source, new FakeTargetResolver(_target),
            new PlaylistMaterializationPlanner(), new TrackMatchDecisionEngine(), _trackMatches, clock,
            new KeyedAsyncLock(), new TestBackendLibraryAccess(_factory, "music"), trackIdentities: links.Object);
        _source.Snapshot = Snapshot("library-link",
            Entry(0, "first", "source-1", "One"), Entry(1, "alias", "source-alias", "One"));

        var refreshed = await service.RefreshAsync(Context(), _link);
        var repeated = await service.RefreshAsync(Context(), _link);

        Assert.Equal(refreshed.SnapshotId, repeated.SnapshotId);
        links.Verify(item => item.LinkLibraryRecordingsAsync(It.IsAny<ProviderActorContext>(), "correlation",
            _canonical, null, It.IsAny<CancellationToken>()), Times.Once);
        await using var saved = _factory.CreateDbContext();
        var linked = await saved.LibraryTracks.SingleAsync(item => item.Id == _trackOne);
        Assert.Equal(_canonical, linked.CanonicalRecordingId);
        Assert.Equal(original.Title, linked.Title);
        Assert.Equal(original.Artist, linked.Artist);
        Assert.Equal(original.DurationMilliseconds, linked.DurationMilliseconds);
        Assert.Equal(original.SourceModifiedAt, linked.SourceModifiedAt);
        Assert.Equal(original.IndexedAt, linked.IndexedAt);
        Assert.Null((await saved.LibraryTracks.SingleAsync(item => item.Id == _trackTwo)).CanonicalRecordingId);
        Assert.Equal(2, await saved.ExternalMetadataSnapshots.CountAsync());
    }

    [Theory]
    [InlineData(ProviderErrorKind.RateLimited)]
    [InlineData(ProviderErrorKind.AccountNeedsReauthentication)]
    public async Task ListenBrainzSyncHonorsRateLimitDelayAndStopsRetryingExpiredTokens(ProviderErrorKind kind)
    {
        var account = await SetPlaylistSourceAccountAsync("listenbrainz");
        var error = new ProviderError(kind, kind == ProviderErrorKind.RateLimited ? TimeSpan.FromSeconds(45) : null);
        var orchestration = new Mock<IPlaylistOrchestrationService>();
        orchestration.Setup(item => item.RunAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<PlaylistOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PlaylistSourceUnavailableException(error.Code, error));
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), new Clock(_now));
        await queue.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
            "playlist.materialize", "source-failure", new(_link, 1022), _user, ProviderAccountId: account, Capability: "playlist"));
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("playlist-worker"));
        var completion = await new PlaylistMaterializationJobHandler(_factory, orchestration.Object, new Clock(_now))
            .ExecuteAsync(new DurableJobExecutionContext(claim, EmptyServices.Instance), default);
        Assert.Equal(kind == ProviderErrorKind.RateLimited ? DurableJobCompletionKind.Retry : DurableJobCompletionKind.Failed, completion.Kind);
        Assert.Equal(error.Code, completion.ErrorCode);
        Assert.Equal(error.RetryAfter, completion.RetryDelay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistRecordingMbidReachesMatchingAndRematchingWithDurationGuards(bool conflictingDuration)
    {
        const string mbid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        await SetLink(mode: PlaylistLinkMode.Virtual);
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            var local = await setup.LibraryTracks.SingleAsync(item => item.Id == _trackTwo);
            local.MusicBrainzRecordingId = mbid.ToUpperInvariant();
            await setup.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot("recording-evidence", Entry(0, "recording", "new-recording", "Different label") with
        {
            MusicBrainzRecordingId = mbid,
            DurationMilliseconds = conflictingDuration ? 300000 : 180000
        });
        await _service.RefreshAsync(Context(), _link);
        Guid externalId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var external = await db.ExternalMetadataSnapshots.SingleAsync();
            externalId = external.Id;
            Assert.Contains(mbid, external.PayloadJson);
            var match = await db.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
            Assert.Equal(!conflictingDuration, match.State == TrackMatchState.Accepted);
            if (!conflictingDuration)
            {
                Assert.Equal(_trackTwo, match.LibraryTrackId);
                Assert.Contains("musicbrainz_recording_id_exact", match.ReasonsJson);
            }
        }
        await _trackMatches.RematchSnapshotAsync(Context(), externalId, "recording-rematch", "playlist-policy-v1");
        await using var verify = await _factory.CreateDbContextAsync();
        var rematched = await verify.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(!conflictingDuration, rematched.State == TrackMatchState.Accepted);
        if (!conflictingDuration) Assert.Equal(_trackTwo, rematched.LibraryTrackId);
    }
}
