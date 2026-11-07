using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlgorithmRolloutProtectsManualChoicesWithoutExemptingKeptAutomaticDecisions(bool pinned)
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
            if (pinned)
                db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ExternalSnapshotId = snapshotId,
                    SourceProviderId = "deezer",
                    SourceExternalIdHash = source.ExternalIdHash,
                    Decision = ManualOverrideDecision.Pin,
                    TargetProviderId = "deezer",
                    TargetExternalId = source.ExternalId,
                    DecisionVersion = 1,
                    Revision = 1,
                    Reason = "Personal choice",
                    CreatedAt = now
                });
            await db.SaveChangesAsync();
        }
        var rematches = new TrackRematchAllService(factory, queue, new SystemPlatformClock());

        Assert.Equal(pinned ? 0 : 1, await rematches.QueueAlgorithmUpgradesAsync());

        await using var verification = factory.CreateDbContext();
        Assert.Equal("retired-v1", (await verification.TrackMatches.SingleAsync()).MatcherVersion);
        Assert.Single(await verification.Set<allstarr.Core.ManagedFiles.RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.Single(await verification.ManagedFiles.ToArrayAsync());
        var authority = await verification.ManualTrackOverrides.SingleOrDefaultAsync();
        Assert.Equal(pinned, authority != null);
        if (authority != null)
        {
            Assert.Equal(1, authority.Revision);
            Assert.Null(authority.RevokedAt);
        }
    }
}
