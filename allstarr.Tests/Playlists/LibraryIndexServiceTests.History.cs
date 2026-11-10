using Moq;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    [Fact]
    public async Task HistoryLibraryCandidatesAreReusedUntilTheIndexChangesAndAccessIsRechecked()
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "lookup", null, null);
        var identity = ReadIdentity(recording.Recording.Id, "spotify", "known-source");
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderTrackIdentities.Add(identity);
            db.ExternalMetadataSnapshots.Add(ReadSnapshot(identity));
            db.LibraryTracks.Add(SelfLoveMatchFixture.Local(_userA, "backend"));
            await db.SaveChangesAsync();
        }
        var reads = new RecordingReadCounter();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options).AddInterceptors(reads).Options);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var service = new LibraryIndexService(factory, state, _clock, _identities, _libraryAccess.Object);
        var lookup = new[] { new LibraryRecordingLookup("external-alias", recording.Recording.Id, null, "apple-musickit", "alias") };
        var first = await service.FindKnownRecordingTracksAsync(viewer, lookup);
        Assert.Single(reads.Commands, command => command.Contains("FROM \"library_tracks\"", StringComparison.Ordinal) &&
            command.Contains("\"FilePath\"", StringComparison.Ordinal));
        reads.Commands.Clear();
        var second = await service.FindKnownRecordingTracksAsync(viewer, lookup);
        Assert.Equal(first, second);
        Assert.DoesNotContain(reads.Commands, command => command.Contains("FROM \"library_tracks\"", StringComparison.Ordinal) &&
            command.Contains("\"FilePath\"", StringComparison.Ordinal));
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.LibraryTracks.SingleAsync();
            track.BackendItemId = "new-native-id";
            track.Revision++;
            await db.SaveChangesAsync();
        }
        Assert.Equal("new-native-id", Assert.Single(await service.FindKnownRecordingTracksAsync(viewer, lookup)).Value);
        _libraryAccess.Setup(item => item.ResolveAsync(viewer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new allstarr.Core.Protocols.BackendLibraryAccess(true, []));
        Assert.Empty(await service.FindKnownRecordingTracksAsync(viewer, lookup));
    }
}
