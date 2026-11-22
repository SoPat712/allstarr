using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    private static readonly LibraryRecordingLookup DeletedSong = new("ext-deezer-song-deleted", null, "USRC17607839", "deezer", "deleted");

    [Fact]
    public async Task ASongThatFailedToPlayAndIsGoneFromTheMediaServerIsNoLongerTheLocalCopyUntilItIsBack()
    {
        var viewer = Context(_userA, "principal-a");
        await _service.UpsertAsync(viewer, Input());
        Assert.Equal("local-item", Assert.Single(await _service.FindKnownRecordingTracksAsync(viewer, [DeletedSong])).Value);
        var server = new GoneScanner("local-item");
        var gone = GoneTracks(server);

        Assert.True(await gone.CheckAsync(viewer, "local-item", default));

        Assert.Equal(["local-item"], Assert.Single(server.Asked));
        Assert.Empty(await _service.FindKnownRecordingTracksAsync(viewer, [DeletedSong]));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Empty(await LibraryTrackAccess.Query(db, viewer, new BackendLibraryAccess(true, ["music"])).ToArrayAsync());
            // The row stays for the add that hands what pointed at it to the copy that replaces it.
            Assert.Single(await LibraryTrackAccess.Query(db, viewer, new BackendLibraryAccess(true, ["music"]), includeGone: true).ToArrayAsync());
        }
        // The media server lists the song again: it is in the library as before.
        _clock.UtcNow = _clock.UtcNow.AddMinutes(20);
        await _service.UpsertAsync(viewer, Input());
        Assert.Equal("local-item", Assert.Single(await _service.FindKnownRecordingTracksAsync(viewer, [DeletedSong])).Value);
    }

    [Fact]
    public async Task ASongTheMediaServerStillHasIsNotMarkedGoneWhenItFailsToPlay()
    {
        var viewer = Context(_userA, "principal-a");
        await _service.UpsertAsync(viewer, Input());

        Assert.False(await GoneTracks(new GoneScanner()).CheckAsync(viewer, "local-item", default));
        Assert.False(await GoneTracks(new GoneScanner { Failure = new HttpRequestException("down") }).CheckAsync(viewer, "local-item", default));

        Assert.Equal("local-item", Assert.Single(await _service.FindKnownRecordingTracksAsync(viewer, [DeletedSong])).Value);
    }

    [Fact]
    public async Task AfterAnIndexPassOnlyTheSongsItDidNotListAreAskedAboutAndOnlyGoneOnesAreForgotten()
    {
        var viewer = Context(_userA, "principal-a");
        await _service.UpsertAsync(viewer, Input());
        await _service.UpsertAsync(viewer, Input() with { BackendItemId = "kept-item", FilePath = "/media/music/artist/kept.flac", Isrc = null });
        await _service.UpsertAsync(viewer, Input() with { BackendItemId = "hidden-item", FilePath = "/media/music/artist/hidden.flac", Isrc = null });
        _clock.UtcNow = _clock.UtcNow.AddMinutes(15);
        var started = _clock.UtcNow;
        await _service.UpsertAsync(viewer, Input() with { BackendItemId = "kept-item", FilePath = "/media/music/artist/kept.flac", Isrc = null });
        // Not listed this pass; the server says one of the two is gone and says nothing of the other.
        var server = new GoneScanner("local-item");

        Assert.Equal(1, await GoneTracks(server).SweepAsync(viewer, started, default));

        Assert.Equal(["hidden-item", "local-item"], Assert.Single(server.Asked).Order(StringComparer.Ordinal).ToArray());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(["hidden-item", "kept-item"], (await LibraryTrackAccess.Query(db, viewer, new BackendLibraryAccess(true, ["music"]))
            .Select(item => item.BackendItemId).ToArrayAsync()).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(await _service.FindKnownRecordingTracksAsync(viewer, [DeletedSong]));
    }

    private LibraryGoneTracks GoneTracks(GoneScanner server) =>
        new(_factory, new BackendLibraryCatalogScannerResolver([server]), _libraryAccess.Object, _clock);

    private sealed class GoneScanner(params string[] gone) : IBackendLibraryCatalogScanner
    {
        internal List<string[]> Asked { get; } = [];
        internal Exception? Failure { get; init; }
        public ProtocolKind Protocol => ProtocolKind.Jellyfin;
        public Task<LibraryCatalogScanResult> ScanAsync(ProtocolExecutionContext context, LibraryCatalogScanRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlySet<string>> GoneAsync(ProtocolExecutionContext context, string backendLibraryId,
            IReadOnlyCollection<string> backendItemIds, CancellationToken cancellationToken)
        {
            Asked.Add(backendItemIds.ToArray());
            if (Failure != null) throw Failure;
            return Task.FromResult<IReadOnlySet<string>>(backendItemIds.Where(gone.Contains).ToHashSet(StringComparer.Ordinal));
        }
    }
}
