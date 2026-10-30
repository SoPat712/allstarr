using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task AlgorithmRolloutLeavesKeptDecisionsUntouched()
    {
        await Service().PublishAsync(Request());
        var snapshotId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = factory.CreateDbContext())
        {
            var source = await db.ProviderTrackIdentities.SingleAsync(item => item.CanonicalRecordingId == recordingId);
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = snapshotId,
                OwnerUserId = owner,
                ProviderAccountId = account,
                ProviderTrackIdentityId = source.Id,
                ProviderId = "deezer",
                ResourceKind = "track",
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = owner.ToString("N"),
                ExternalIdHash = source.ExternalIdHash,
                SnapshotVersion = 1,
                PayloadSha256 = new string('a', 64),
                RetrievedAt = now
            });
            db.TrackMatches.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ExternalSnapshotId = snapshotId,
                OwnerUserId = owner,
                CanonicalRecordingId = recordingId,
                State = TrackMatchState.Accepted,
                MatcherVersion = "retired-v1",
                DecisionVersion = 1,
                DecidedAt = now
            });
            await db.SaveChangesAsync();
        }
        var rematches = new TrackRematchAllService(factory, queue, new SystemPlatformClock());

        Assert.Equal(0, await rematches.QueueAlgorithmUpgradesAsync());

        await using var verification = factory.CreateDbContext();
        Assert.Equal("retired-v1", (await verification.TrackMatches.SingleAsync()).MatcherVersion);
        Assert.Single(await verification.Set<allstarr.Core.ManagedFiles.RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.Single(await verification.ManagedFiles.ToArrayAsync());
    }
}
