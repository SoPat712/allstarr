using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task PlaylistResponseMembership_UsesOneVisibleListInsteadOfReadingEveryNativePlaylist()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PlaylistLinks.SingleAsync()).TargetPlaylistId = "native-0";
            for (var index = 1; index < 18; index++)
            {
                var link = Link();
                link.Id = Guid.CreateVersion7();
                link.SourcePlaylistId = "playlist-" + index;
                link.SourcePlaylistIdHash = Hash(link.SourcePlaylistId);
                link.TargetPlaylistId = "native-" + index;
                db.PlaylistLinks.Add(link);
            }
            await db.SaveChangesAsync();
        }
        var reads = 0;
        var target = new Mock<IBackendPlaylistTarget>();
        target.Setup(item => item.ReadAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (BackendPlaylistTargetContext _, string id, CancellationToken token) =>
            {
                reads++;
                await Task.Delay(160, token);
                return new BackendPlaylistTargetResult<BackendPlaylistSnapshot>(BackendPlaylistTargetStatus.Success,
                    new(id, "Native", [], "fingerprint"));
            });
        target.Setup(item => item.ListPageAsync(It.IsAny<BackendPlaylistTargetContext>(), null, 0, 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>(BackendPlaylistTargetStatus.Success,
                Enumerable.Range(0, 18).Select(index => new BackendPlaylistSummary("native-" + index, "Native")).ToArray()));
        var targets = new Mock<IBackendPlaylistTargetResolver>();
        targets.Setup(item => item.Resolve("jellyfin")).Returns(target.Object);
        var reader = new SavedLibraryReader(_factory, targets.Object);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(await reader.ReadAsync(Context()));
        timer.Stop();
        Assert.True(reads == 0, $"Membership reconstruction read {reads} full native playlists in {timer.ElapsedMilliseconds} ms.");
        target.Verify(item => item.ListPageAsync(It.IsAny<BackendPlaylistTargetContext>(), null, 0, 200, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(PlaylistLinkMode.Virtual, true)]
    [InlineData(PlaylistLinkMode.Hybrid, true)]
    [InlineData(PlaylistLinkMode.Materialized, false)]
    public async Task VirtualPlaylistSummaries_PreserveModeVisibilityWithoutATarget(PlaylistLinkMode mode, bool visible)
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("mode-summary", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 1));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            link.Mode = mode;
            link.TargetPlaylistId = null;
            await db.SaveChangesAsync();
        }
        var access = new Mock<IBackendLibraryAccessResolver>(MockBehavior.Strict);
        var service = new PlaylistVirtualizationService(_factory, new(_factory, access.Object), access.Object);
        Assert.Equal(visible ? 1 : 0, (await service.ListAsync(Context())).Count);
        access.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VirtualPlaylistList_ReadsSummariesWithoutTrackProjection()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("summary", Entry(0, "one", "source-1", "One"), Entry(1, "two", "missing", "Two"));
        await _service.RunAsync(Context(), new(_link, 1));
        var access = new Mock<IBackendLibraryAccessResolver>(MockBehavior.Strict);
        var service = new PlaylistVirtualizationService(_factory, new(_factory, access.Object), access.Object);
        var rows = await service.ListAsync(Context());
        var row = Assert.Single(rows);
        Assert.Empty(row.Tracks);
        Assert.Equal(2, row.TrackCount);
        Assert.True(row.DurationMilliseconds > 0);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, row.CreatedAt);
        access.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VirtualPlaylistDurations_UseMatchedRecordingWhenSnapshotHasNoDuration()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("duration-summary", Entry(0, "one", "source-1", "One") with { DurationMilliseconds = null });
        await _service.RunAsync(Context(), new(_link, 1));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.CanonicalRecordings.SingleAsync(item => item.Id == _canonical)).DurationMilliseconds = 187654;
            foreach (var track in await db.LibraryTracks.ToListAsync()) track.DurationMilliseconds = null;
            await db.SaveChangesAsync();
        }
        var access = new TestBackendLibraryAccess(_factory, "music");
        var service = new PlaylistVirtualizationService(_factory, new(_factory, access), access);
        var summary = Assert.Single(await service.ListAsync(Context()));
        Assert.Empty(summary.Tracks);
        var detail = Assert.IsType<VirtualPlaylistReadModel>(await service.ReadAsync(Context(), summary.ProtocolId));
        Assert.Equal(187654, summary.DurationMilliseconds);
        Assert.Equal(summary.DurationMilliseconds, detail.DurationMilliseconds);
        Assert.Equal(detail.DurationMilliseconds, detail.Tracks.Sum(track => track.DurationMilliseconds ?? 0));
    }

    [Fact]
    public async Task HomePlaylistCountsMatchPublishedProjectionsAndResolveViewerAccessOnce()
    {
        _source.Snapshot = Snapshot("home-counts",
            Entry(0, "one", "source-1", "One"),
            Entry(1, "missing", "missing", "Missing"));
        await _service.RunAsync(Context(), new(_link, 1));
        var second = Link();
        second.Id = Guid.CreateVersion7();
        second.SourcePlaylistId = "second";
        second.SourcePlaylistIdHash = Hash("second");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PlaylistLinks.Add(second);
            await db.SaveChangesAsync();
        }
        await _service.RunAsync(Context(), new(second.Id, 1));
        var realAccess = new TestBackendLibraryAccess(_factory, "music");
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveUserAsync(_user, It.IsAny<CancellationToken>()))
            .Returns((Guid user, CancellationToken token) => realAccess.ResolveUserAsync(user, token));
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["fixture"]);
        var reader = new DurablePlaylistProjectionReader(_factory, access.Object, gateway.Object);
        Guid[] links = [_link, second.Id, Guid.CreateVersion7()];
        var full = await reader.ReadByLinkIdsAsync(_user, links, viewerUserId: _user);
        access.Invocations.Clear();
        gateway.Invocations.Clear();

        var counts = await reader.ReadHomeCountsAsync(_user, links, _user);

        Assert.Equal(full.Values.Sum(item => item.PlayableCount), counts.PlayableTracks);
        Assert.Equal(full.Values.Sum(item => item.MissingCount), counts.UnresolvedTracks);
        Assert.True(counts.PlayableTracks > 0);
        Assert.True(counts.UnresolvedTracks > 0);
        access.Verify(item => item.ResolveUserAsync(_user, It.IsAny<CancellationToken>()), Times.Once);
        gateway.Verify(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal((0, 0), await reader.ReadHomeCountsAsync(Guid.CreateVersion7(), links, _user));
        Assert.Equal((0, 0), await reader.ReadHomeCountsAsync(_user, [], _user));
    }

    [Theory]
    [InlineData(PlaylistLinkMode.Materialized)]
    [InlineData(PlaylistLinkMode.Hybrid)]
    public async Task NativePlaylistProjection_UsesPublishedSourceOrderAndDropsStaleTargetRows(PlaylistLinkMode mode)
    {
        const string targetId = "4383a46d8bcac3be2ef9385053ea18df";
        await SetLink(mode: mode);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PlaylistLinks.SingleAsync()).TargetPlaylistId = targetId;
            await db.SaveChangesAsync();
        }
        _target.Seed(targetId, ["local-2"]);
        _source.Snapshot = Snapshot("projection-refresh",
            Entry(0, "external-entry", "source-external", "External"),
            Entry(1, "local-entry", "source-1", "One"),
            Entry(2, "missing-entry", "missing", "Missing"));
        var refresh = await _service.RefreshAsync(Context(), _link);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var external = await db.ExternalMetadataSnapshots.SingleAsync(item => item.ExternalIdHash == Hash("source-external"));
            var pin = Override(external.Id, ManualOverrideDecision.Pin, null);
            pin.TargetProviderId = "deezer";
            pin.TargetExternalId = "external-song";
            db.ManualTrackOverrides.Add(pin);
            await db.SaveChangesAsync();
        }
        await _service.RunAsync(Context(), new(_link, 1, refresh.SnapshotId));
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        var access = new TestBackendLibraryAccess(_factory, "music");
        var service = new PlaylistVirtualizationService(_factory,
            new DurablePlaylistProjectionReader(_factory, access, gateway.Object), access, new FakeTargetResolver(_target));

        _target.ArtworkReference = "current-native-cover";
        var native = await service.ReadAsync(Context(), targetId);
        Assert.NotNull(native);
        Assert.Equal("current-native-cover", native.ArtworkReferenceKey);
        Assert.Equal(["ext-deezer-song-external-song", "local-1"], native.Tracks
            .Where(item => item.RouteKind != TrackRouteKind.Unresolved).Select(item => item.BackendItemId));
        Assert.DoesNotContain(native.Tracks, item => item.BackendItemId == "local-2");
        Assert.Equal([0, 1, 2], native.Tracks.Select(item => item.SourcePosition));
        Assert.Equal(targetId, native.TargetPlaylistId);
        var virtualDetail = await service.ReadAsync(Context(), PlaylistVirtualizationService.CreateProtocolId(_link));
        Assert.NotNull(virtualDetail);
        Assert.Equal(native.ArtworkReferenceKey, virtualDetail.ArtworkReferenceKey);
        Assert.Equal(targetId, Assert.Single(await service.ListAsync(Context())).TargetPlaylistId);

        _target.ReadStatus = allstarr.Core.Playlists.Targets.BackendPlaylistTargetStatus.Unauthorized;
        Assert.Null(await service.ReadAsync(Context(), targetId));
        Assert.Empty(await service.ListAsync(Context()));
    }
}
