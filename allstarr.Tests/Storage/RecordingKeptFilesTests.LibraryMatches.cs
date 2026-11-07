using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task KeepingAFileDoesNotProtectAnAutomaticMatchFromLibraryReevaluation()
    {
        await Service().PublishAsync(Request());
        var now = DateTimeOffset.UtcNow;
        await using (var db = factory.CreateDbContext())
        {
            var snapshot = new ExternalMetadataSnapshotRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ProviderAccountId = account,
                ProviderId = "deezer",
                ResourceKind = "track",
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = owner.ToString("N"),
                ExternalIdHash = new string('a', 64),
                PayloadSha256 = new string('b', 64),
                SnapshotVersion = 1,
                RetrievedAt = now,
                PayloadJson = JsonSerializer.Serialize(new { Title = "Fixture", Artist = "Artist" })
            };
            db.ExternalMetadataSnapshots.Add(snapshot);
            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ExternalSnapshotId = snapshot.Id,
                CanonicalRecordingId = recordingId,
                State = TrackMatchState.Accepted,
                Confidence = .99,
                Threshold = .88,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                MatcherVersion = "previous",
                DecidedAt = now
            });
            await db.SaveChangesAsync();
        }
        var rematches = new TrackRematchAllService(factory, queue, new SystemPlatformClock());

        Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());

        await using var saved = factory.CreateDbContext();
        Assert.Single(await saved.Set<allstarr.Core.ManagedFiles.RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.Empty(await saved.ManualTrackOverrides.ToArrayAsync());
    }
}
