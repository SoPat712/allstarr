using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task VirtualPlaylistWarmReadsReuseRecordingIdentitiesAndKeepMembershipAndListenerChangesFresh()
    {
        publish = true;
        await Seed(count: 100, plays: 0);
        var saved = await service.SaveListAsync(owner, Guid.NewGuid(), "Saved songs", "Songs in order", songs.Values.ToArray(), "Picked by you");
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var identityBatches = 0;
        identities.Setup(item => item.ResolveRecordingsAsync(It.IsAny<ProviderActorContext>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProviderActorContext _, IReadOnlyCollection<string> ids, CancellationToken token) =>
            {
                identityBatches++;
                Assert.InRange(ids.Count, 98, 100);
                await Task.Delay(150, token);
                return (IReadOnlyDictionary<string, CanonicalRecordingIdentity?>)new Dictionary<string, CanonicalRecordingIdentity?>();
            });
        var library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        var matchBatches = 0;
        library.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((allstarr.Core.Protocols.ProtocolExecutionContext _, IReadOnlyList<LibraryRecordingLookup> ids, CancellationToken _) =>
            {
                matchBatches++;
                Assert.InRange(ids.Count, 98, 100);
                return new Dictionary<string, string>();
            });
        using var cache = new MemoryApplicationCache(clock);
        var uncached = NewService(libraryIndex: library.Object, identities: identities.Object);
        var cached = NewService(libraryIndex: library.Object, identities: identities.Object, recordingCache: cache);
        var watch = Stopwatch.StartNew();
        var before = JsonSerializer.Serialize(await uncached.PublishedAsync(Context(owner), default, saved.Id));
        var baseline = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var first = JsonSerializer.Serialize(await cached.PublishedAsync(Context(owner), default, saved.Id));
        var cold = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(before, first);
        Assert.Equal(2, identityBatches);
        watch.Restart();
        var warm = JsonSerializer.Serialize(await cached.PublishedAsync(Context(owner), default, saved.Id));
        var elapsed = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(before, warm);
        Assert.Equal(2, identityBatches);
        Assert.Equal(3, matchBatches);

        await taste.PutNudgeAsync(owner, new("artist", songs["0"].Artist, "never"));
        Assert.DoesNotContain(Assert.Single(await cached.PublishedAsync(Context(owner), default, saved.Id)).Tracks,
            item => item.Title == songs["0"].Title);
        Assert.Equal(2, identityBatches);
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.ResourceKind == "personal-mixes")
                .OrderByDescending(item => item.SnapshotVersion).FirstAsync();
            var document = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
            var selected = document.Mixes.Single(item => item.Value.Id == saved.Id);
            document.Mixes[selected.Key] = selected.Value with { Songs = selected.Value.Songs.Take(99).ToArray() };
            row.Id = Guid.NewGuid();
            row.SnapshotVersion++;
            row.PayloadJson = JsonSerializer.Serialize(document);
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        var changed = Assert.Single(await cached.PublishedAsync(Context(owner), default, saved.Id));
        Assert.Equal(98, changed.Tracks.Count);
        Assert.DoesNotContain(changed.Tracks, item => item.Title == songs["99"].Title);
        Assert.Equal(2, identityBatches);
        await cached.SetInAppsAsync(owner, saved.Id, false, default);
        Assert.Empty(await cached.PublishedAsync(Context(owner), default, saved.Id));
        output.WriteLine($"100-song recording identity lookup, fake 150 ms batch: uncached {baseline:F1} ms; cold {cold:F1} ms; warm {elapsed:F1} ms; identity batches 1 -> 0; output bytes equal; current listener filters and membership read on every request.");
    }
}
