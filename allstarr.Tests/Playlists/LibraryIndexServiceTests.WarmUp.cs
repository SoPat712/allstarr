using System.Data.Common;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    [Fact]
    public async Task OwnershipWarmUpFillsTheMergedSearchCandidatesWithoutChangingOutput()
    {
        var (viewer, recording) = await SeedOwnershipAsync();
        var time = new ManualTimeProvider();
        var reads = new OwnershipReadGate { Time = time, Delay = TimeSpan.FromMilliseconds(20_700) };
        var service = OwnershipService(reads);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Self Love", 20, 0, 0, null))
            .Returns(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1800), time);
                return new SearchResult
                {
                    Songs = [new Song { Id = "external-alias", CanonicalRecordingId = recording,
                        ExternalProvider = "apple-musickit", ExternalId = "alias", Title = "Self Love", Artist = "Artist" },
                        new Song { Id = "unowned", Title = "Self Love", Artist = "Other artist" }]
                };
            });
        var native = new Mock<INativeMusicSearch<string>>();
        native.Setup(item => item.SearchAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        native.Setup(item => item.GetSongsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new(MusicItemKind.Song, SelfLoveMatchFixture.BackendItemId, "Self Love", "Artist", "unchanged payload")]);
        var request = new MusicSearchRequest("Self Love", new(20), MusicSearchWindow.None, MusicSearchWindow.None, MusicSearchWindow.None);
        var coldStart = time.GetUtcNow();
        var coldSearch = new MusicSearchService(gateway.Object, service, factory: _factory).SearchAsync(viewer, request, native.Object);
        await AdvanceAsync(TimeSpan.FromMilliseconds(1800));
        await AdvanceAsync(TimeSpan.FromMilliseconds(20_700));
        var cold = await coldSearch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(22_500), time.GetUtcNow() - coldStart);
        Assert.Equal(1, reads.Reads);
        Assert.Equal(SelfLoveMatchFixture.BackendItemId, cold.Songs[0].Native?.Id);

        service = OwnershipService(reads);
        var warming = service.WarmOwnershipAsync(viewer);
        await AdvanceAsync(TimeSpan.FromMilliseconds(20_700));
        await warming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reads.Reads);
        var warmStart = time.GetUtcNow();
        var warmedMusic = new MusicSearchService(gateway.Object, service, factory: _factory);
        var warmSearch = warmedMusic.SearchAsync(viewer, request, native.Object);
        await AdvanceAsync(TimeSpan.FromMilliseconds(1800));
        var warmed = await warmSearch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(1800), time.GetUtcNow() - warmStart);
        Assert.Equal(2, reads.Reads);
        Assert.Equal(JsonSerializer.Serialize(cold), JsonSerializer.Serialize(warmed));
        var repeatStart = time.GetUtcNow();
        var repeatedSearch = warmedMusic.SearchAsync(viewer, request, native.Object);
        await AdvanceAsync(TimeSpan.FromMilliseconds(1800));
        var repeated = await repeatedSearch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(1800), time.GetUtcNow() - repeatStart);
        Assert.Equal(2, reads.Reads);
        Assert.Equal(JsonSerializer.Serialize(cold), JsonSerializer.Serialize(repeated));
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Self Love", 20, 0, 0, null), Times.Exactly(3));

        async Task AdvanceAsync(TimeSpan delay)
        {
            Assert.Equal(delay, await time.NextTimerAsync());
            time.Advance(delay);
        }
    }

    [Fact]
    public async Task OwnershipSearchJoinsWarmUpAndSurvivesAWarmUpWaiterCancellation()
    {
        var (viewer, recording) = await SeedOwnershipAsync();
        var gate = new OwnershipReadGate { Block = true };
        var service = OwnershipService(gate);
        using var stop = new CancellationTokenSource();
        var warming = service.WarmOwnershipAsync(viewer, stop.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var searching = service.FindKnownRecordingTracksAsync(viewer,
            [new("external-alias", recording, null, "apple-musickit", "alias")]);
        Assert.False(searching.IsCompleted);
        Assert.Equal(1, gate.Reads);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
        gate.Release.TrySetResult();
        Assert.Equal(SelfLoveMatchFixture.BackendItemId,
            Assert.Single(await searching.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        Assert.Equal(1, gate.Reads);
    }

    [Fact]
    public async Task OwnershipWarmUpInvalidatesOnLibraryWritesAndAccessChanges()
    {
        var (viewer, recording) = await SeedOwnershipAsync();
        var reads = new OwnershipReadGate();
        var service = OwnershipService(reads);
        var lookup = new LibraryRecordingLookup[] { new("external-alias", recording, null, "apple-musickit", "alias") };
        await service.WarmOwnershipAsync(viewer);
        Assert.Single(await service.FindKnownRecordingTracksAsync(viewer, lookup));
        Assert.Equal(1, reads.Reads);
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.LibraryTracks.SingleAsync();
            track.IndexedAt = _clock.UtcNow.AddMinutes(1);
            track.DurationRetrievedAt = _clock.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync();
        }
        Assert.Single(await service.FindKnownRecordingTracksAsync(viewer, lookup));
        Assert.Equal(1, reads.Reads);
        await using (var db = _factory.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var track = await db.LibraryTracks.SingleAsync();
            track.BackendItemId = "updated-copy";
            await db.SaveChangesAsync();
            Assert.Equal(SelfLoveMatchFixture.BackendItemId, Assert.Single(await service.FindKnownRecordingTracksAsync(viewer, lookup)).Value);
            Assert.Equal(1, reads.Reads);
            await transaction.CommitAsync();
        }
        Assert.Equal("updated-copy", Assert.Single(await service.FindKnownRecordingTracksAsync(viewer, lookup)).Value);
        Assert.Equal(2, reads.Reads);
        _libraryAccess.Setup(item => item.ResolveAsync(viewer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        Assert.Empty(await service.FindKnownRecordingTracksAsync(viewer, lookup));
        _libraryAccess.Setup(item => item.ResolveAsync(viewer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        await using (var db = _factory.CreateDbContext())
        {
            db.LibraryTracks.Remove(await db.LibraryTracks.SingleAsync());
            await db.SaveChangesAsync();
        }
        Assert.Empty(await service.FindKnownRecordingTracksAsync(viewer, lookup));
        Assert.Equal(3, reads.Reads);
        await using (var db = _factory.CreateDbContext())
        {
            db.LibraryTracks.Add(SelfLoveMatchFixture.Local(_userA, "backend"));
            await db.SaveChangesAsync();
        }
        Assert.Single(await service.FindKnownRecordingTracksAsync(viewer, lookup));
        Assert.Equal(4, reads.Reads);
    }

    [Fact]
    public async Task OwnershipSearchResultCacheInvalidatesAfterLibraryChanges()
    {
        var (viewer, recording) = await SeedOwnershipAsync();
        var service = OwnershipService(new OwnershipReadGate());
        var native = new Mock<INativeMusicSearch<string>>();
        native.Setup(item => item.GetSongsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> ids, CancellationToken _) => ids.Select(id =>
                new NativeMusicItem<string>(MusicItemKind.Song, id, "Self Love", "Artist", id)).ToArray());
        var music = new MusicSearchService(Mock.Of<IProtocolProviderGateway>(), service, factory: _factory);
        var songs = new Song[] { new() { Id = "external-alias", CanonicalRecordingId = recording,
            ExternalProvider = "apple-musickit", ExternalId = "alias", Title = "Self Love", Artist = "Artist" } };
        Assert.Equal(SelfLoveMatchFixture.BackendItemId,
            Assert.Single(await music.ProjectSongsAsync(viewer, songs, native.Object, cacheSearch: true)).Native?.Id);
        await using (var db = _factory.CreateDbContext())
        {
            (await db.LibraryTracks.SingleAsync()).BackendItemId = "updated-copy";
            await db.SaveChangesAsync();
        }
        Assert.Equal("updated-copy",
            Assert.Single(await music.ProjectSongsAsync(viewer, songs, native.Object, cacheSearch: true)).Native?.Id);
    }

    private async Task<(ProtocolExecutionContext Viewer, Guid Recording)> SeedOwnershipAsync()
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "lookup", null, null);
        var identity = ReadIdentity(recording.Recording.Id, "spotify", "known-source");
        await using var db = _factory.CreateDbContext();
        db.ProviderTrackIdentities.Add(identity);
        db.ExternalMetadataSnapshots.Add(ReadSnapshot(identity));
        db.LibraryTracks.Add(SelfLoveMatchFixture.Local(_userA, "backend"));
        await db.SaveChangesAsync();
        return (viewer, recording.Recording.Id);
    }

    private LibraryIndexService OwnershipService(OwnershipReadGate reads)
    {
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(reads).Options);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        return new(factory, state, _clock, _identities, _libraryAccess.Object);
    }

    private sealed class OwnershipReadGate : DbCommandInterceptor
    {
        public bool Block { get; init; }
        public TimeProvider? Time { get; init; }
        public TimeSpan Delay { get; init; }
        public int Reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM \"library_tracks\"", StringComparison.Ordinal)) return result;
            Interlocked.Increment(ref Reads);
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            if (Time != null) await Task.Delay(Delay, Time, cancellationToken);
            return result;
        }
    }
}
