using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task APlaylistSongWhoseLibraryCopyIsGonePlaysFromItsServiceCopyUntilTheCopyIsBack()
    {
        _source.Snapshot = Snapshot("gone-copy", Entry(0, "one", "source-1", "One"), Entry(1, "two", "source-2", "Two"));
        await _service.RunAsync(Context(), new(_link, 1));
        Assert.Equal(["local-1", "local-2"], _target.LastWrite!.OrderedBackendItemIds);
        var (reader, virtualization) = GoneReaders();
        Assert.Equal(["local-1", "local-2"], (await reader.ReadByLinkIdAsync(_user, _link))!.Entries.Select(item => item.BackendItemId));
        var decisions = await DecisionCount();
        var writes = _target.WriteCalls;

        // Both library songs are deleted from the media server; only the first has a service copy to fall back to.
        var server = new GoneServer("local-1", "local-2");
        Assert.True(await GoneTracks(server).CheckAsync(Context(), "local-1", default));
        Assert.True(await GoneTracks(server).CheckAsync(Context(), "local-2", default));

        foreach (var entries in new[]
                 {
                     (await reader.ReadByLinkIdAsync(_user, _link))!.Entries,
                     (await reader.ReadByLinkIdsAsync(_user, [_link]))[_link].Entries
                 })
        {
            Assert.Equal(2, entries.Count);
            Assert.Equal(("external", "fixture", (string?)null), (entries[0].RouteKind, entries[0].RouteProviderId, entries[0].BackendItemId));
            Assert.Equal(TrackMatchState.Accepted, entries[0].MatchState);
            Assert.Equal(("unmatched", (string?)null), (entries[1].RouteKind, entries[1].BackendItemId));
            Assert.Equal("Two", entries[1].Title);
        }
        var shown = (await virtualization.ReadAsync(Context(), PlaylistVirtualizationService.CreateProtocolId(_link)))!.Tracks;
        Assert.Equal([TrackRouteKind.External, TrackRouteKind.Unresolved], shown.Select(item => item.RouteKind));
        Assert.Contains(shown[0].BackendItemId, new[] { "ext-fixture-song-source-1", "ext-fixture-song-source-alias" });
        Assert.Equal("One", shown[0].Title);
        // Nothing is rematched or rewritten for it: the stored matches stay and only reads change.
        Assert.Equal(decisions, await DecisionCount());
        Assert.Equal(writes, _target.WriteCalls);
        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Empty(await db.Jobs.ToArrayAsync());

        await ListedAgainAsync(_trackOne, _trackTwo);
        Assert.Equal([("local", "local-1"), ("local", "local-2")], (await reader.ReadByLinkIdAsync(_user, _link))!.Entries
            .Select(item => (item.RouteKind, item.BackendItemId)));
        Assert.Equal(["local-1", "local-2"], (await virtualization.ReadAsync(Context(), PlaylistVirtualizationService.CreateProtocolId(_link)))!
            .Tracks.Select(item => item.BackendItemId));

        // A sync while a song is gone leaves it out of the media server's playlist, and the one after it is back puts it back.
        Assert.True(await GoneTracks(server).CheckAsync(Context(), "local-1", default));
        await _service.RunAsync(Context(), new(_link, 2));
        Assert.Equal(["local-2"], _target.LastWrite!.OrderedBackendItemIds);
        await ListedAgainAsync(_trackOne);
        await _service.RunAsync(Context(), new(_link, 3));
        Assert.Equal(["local-1", "local-2"], _target.LastWrite!.OrderedBackendItemIds);
    }

    [Fact]
    public async Task APinToALibrarySongThatIsGoneFallsBackToTheServiceAndAppliesAgainWhenTheSongIsBack()
    {
        _source.Snapshot = Snapshot("gone-pin", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 1));
        Guid pinId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var external = await db.ExternalMetadataSnapshots.SingleAsync(item => item.ExternalIdHash == Hash("source-1"));
            var pin = Override(external.Id, ManualOverrideDecision.Pin, _trackOne);
            pinId = pin.Id;
            db.ManualTrackOverrides.Add(pin);
            await db.SaveChangesAsync();
        }
        var (reader, virtualization) = GoneReaders();
        var pinned = Assert.Single((await reader.ReadByLinkIdAsync(_user, _link))!.Entries);
        Assert.Equal(("local", "local-1", TrackMatchState.Pinned), (pinned.RouteKind, pinned.BackendItemId, pinned.MatchState));

        Assert.True(await GoneTracks(new GoneServer("local-1")).CheckAsync(Context(), "local-1", default));

        foreach (var entry in new[]
                 {
                     Assert.Single((await reader.ReadByLinkIdAsync(_user, _link))!.Entries),
                     Assert.Single((await reader.ReadByLinkIdsAsync(_user, [_link]))[_link].Entries)
                 })
            Assert.Equal(("external", "fixture", (string?)null), (entry.RouteKind, entry.RouteProviderId, entry.BackendItemId));
        Assert.Equal(TrackRouteKind.External, Assert.Single((await virtualization.ReadAsync(Context(),
            PlaylistVirtualizationService.CreateProtocolId(_link)))!.Tracks).RouteKind);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var kept = await db.ManualTrackOverrides.SingleAsync(item => item.Id == pinId);
            Assert.Null(kept.RevokedAt);
            Assert.Equal(_trackOne, kept.LibraryTrackId);
        }

        await ListedAgainAsync(_trackOne);
        var back = Assert.Single((await reader.ReadByLinkIdAsync(_user, _link))!.Entries);
        Assert.Equal(("local", "local-1", TrackMatchState.Pinned), (back.RouteKind, back.BackendItemId, back.MatchState));
    }

    private (DurablePlaylistProjectionReader Reader, PlaylistVirtualizationService Virtualization) GoneReaders()
    {
        var access = new TestBackendLibraryAccess(_factory, "music");
        var reader = new DurablePlaylistProjectionReader(_factory, access);
        return (reader, new PlaylistVirtualizationService(_factory, reader, access, new FakeTargetResolver(_target)));
    }

    private LibraryGoneTracks GoneTracks(GoneServer server) => new(_factory,
        new BackendLibraryCatalogScannerResolver([server]), new TestBackendLibraryAccess(_factory, "music"), new Clock(_now.AddMinutes(10)));

    // The next index pass lists the song again.
    private async Task ListedAgainAsync(params Guid[] ids)
    {
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var track in await db.LibraryTracks.Where(item => ids.Contains(item.Id)).ToArrayAsync())
        {
            Assert.True(LibraryTrackRecord.IsGone(track.IndexedAt));
            track.IndexedAt = _now.AddMinutes(2);
            track.Revision++;
        }
        await db.SaveChangesAsync();
    }

    private sealed class GoneServer(params string[] gone) : IBackendLibraryCatalogScanner
    {
        public ProtocolKind Protocol => ProtocolKind.Jellyfin;
        public Task<LibraryCatalogScanResult> ScanAsync(ProtocolExecutionContext context, LibraryCatalogScanRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlySet<string>> GoneAsync(ProtocolExecutionContext context, string backendLibraryId,
            IReadOnlyCollection<string> backendItemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(backendItemIds.Where(gone.Contains).ToHashSet(StringComparer.Ordinal));
    }
}
