using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task AcceptedPublicationBatchesRebuildAroundANewManualChoiceWithoutChangingOrder()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        _source.Snapshot = Snapshot("publication-batches", Enumerable.Range(0, 51)
            .Select(index => Entry(index, $"entry-{index}", $"source-{index}", "One")).ToArray());
        await _service.RefreshAsync(Context(), _link);
        TrackMatchRecord[] old;
        await using (var db = _factory.CreateDbContext())
        {
            old = await db.TrackMatches.OrderBy(item => item.ExternalSnapshotId).ToArrayAsync();
            Assert.Equal(51, old.Length);
            foreach (var decision in old)
            {
                decision.LibraryTrackId = null;
                decision.State = TrackMatchState.Ambiguous;
                decision.MatcherVersion = "retired-matcher";
            }
            await db.SaveChangesAsync();
        }
        var accepted = new Dictionary<Guid, Guid>();
        foreach (var decision in old)
        {
            var current = await _trackMatches.RecordDecisionAsync(Context(), new MatchDecisionInput(
                decision.ExternalSnapshotId, _trackOne, _canonical, TrackMatchState.Accepted, .99,
                decision.Threshold, decision.DecisionVersion + 1, decision.SourceSnapshotVersion,
                decision.LibraryIndexRevision, TrackMatchDecisionEngine.AlgorithmVersion,
                decision.PolicyVersion, "[]", "[]", "[]"));
            accepted[decision.ExternalSnapshotId] = current.Id;
        }
        var options = new DurableJobOptions();
        var clock = new Clock(_now);
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), clock);
        var publication = new PlaylistMatchPublication(_factory, queue, _service, clock);
        Assert.Equal(2, await publication.QueueAsync());
        var repository = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        var dispatcher = new TrackRematchJobHandler(null!, null!, repository.Object, publication);
        var protectedDecision = old.OrderBy(item => item.ExternalSnapshotId).First();
        await using (var db = _factory.CreateDbContext())
        {
            db.ManualTrackOverrides.Add(Override(protectedDecision.ExternalSnapshotId, ManualOverrideDecision.Reject, null));
            await db.SaveChangesAsync();
        }
        var completed = 0;
        while (await queue.ClaimNextAsync("publication-batches", [TrackRematchJobHandler.Type]) is { } claim)
        {
            Assert.True(++completed <= 6, "Publication must settle in a bounded number of jobs.");
            var result = await dispatcher.ExecuteAsync(new(claim, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await queue.CompleteAsync(claim, result);
        }
        Assert.Equal(3, completed);
        repository.VerifyNoOtherCalls();
        Assert.Equal(0, await publication.QueueAsync());
        await using var saved = _factory.CreateDbContext();
        var entries = await saved.PlaylistSourceEntries.OrderBy(item => item.SourcePosition).ToArrayAsync();
        Assert.Equal(Enumerable.Range(0, 51), entries.Select(item => item.SourcePosition));
        Assert.All(entries, entry => Assert.Equal(entry.ExternalMetadataSnapshotId == protectedDecision.ExternalSnapshotId
            ? protectedDecision.Id : accepted[entry.ExternalMetadataSnapshotId], entry.PublishedTrackMatchId));
        Assert.Single(await saved.ManualTrackOverrides.ToArrayAsync());
        Assert.Equal(0, _target.WriteCalls);
    }

    [Theory]
    [InlineData("jellyfin", "none")]
    [InlineData("jellyfin", "source")]
    [InlineData("jellyfin", "account")]
    [InlineData("jellyfin", "link")]
    [InlineData("jellyfin", "disabled")]
    [InlineData("subsonic", "none")]
    [InlineData("subsonic", "source")]
    [InlineData("subsonic", "account")]
    [InlineData("opensubsonic", "none")]
    [InlineData("subsonic", "link")]
    [InlineData("subsonic", "disabled")]
    [InlineData("jellyfin", "manual")]
    [InlineData("subsonic", "manual")]
    public async Task AcceptedMatchPublicationAdvancesStaleReviewWithoutChangingManualAuthority(string protocol, string change)
    {
        var manualAfterQueue = change == "manual";
        var backend = protocol == "jellyfin" ? "jellyfin" : "subsonic";
        var kind = backend == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic;
        await SetLink(mode: PlaylistLinkMode.Virtual);
        await using (var db = _factory.CreateDbContext())
        {
            (await db.Users.SingleAsync()).BackendType = backend;
            (await db.PlaylistLinks.SingleAsync()).TargetProtocol = backend;
            foreach (var track in await db.LibraryTracks.ToArrayAsync()) track.Protocol = backend;
            await db.SaveChangesAsync();
        }
        var execution = new ProtocolExecutionContext(kind, "backend", "principal",
            new allstarr.Core.Identity.AllstarrPrincipal(_user, backend, "backend", "principal", "Owner", false),
            "publication", _now.AddMinutes(5), default);
        _source.Snapshot = Snapshot("publication", Entry(0, "entry-1", "source-1", "One"));
        await _service.RefreshAsync(execution, _link);
        TrackMatchRecord old;
        Guid entryId;
        Guid? selectedLibraryTrack;
        await using (var db = _factory.CreateDbContext())
        {
            old = await db.TrackMatches.SingleAsync();
            entryId = (await db.PlaylistSourceEntries.SingleAsync()).Id;
            selectedLibraryTrack = old.LibraryTrackId ?? _trackOne;
            old.LibraryTrackId = null;
            old.State = TrackMatchState.Ambiguous;
            old.MatcherVersion = "retired-matcher";
            await db.SaveChangesAsync();
        }
        var accepted = await _trackMatches.RecordDecisionAsync(execution, new MatchDecisionInput(
            old.ExternalSnapshotId, selectedLibraryTrack, old.CanonicalRecordingId, TrackMatchState.Accepted,
            .99, old.Threshold, old.DecisionVersion + 1, old.SourceSnapshotVersion, old.LibraryIndexRevision,
            TrackMatchDecisionEngine.AlgorithmVersion, old.PolicyVersion, "[]", "[]", "[]"));
        if (protocol == "opensubsonic")
        {
            await using var legacy = _factory.CreateDbContext();
            await legacy.ExternalMetadataSnapshots.ExecuteUpdateAsync(update => update.SetProperty(item => item.Protocol, "opensubsonic"));
        }
        var options = new DurableJobOptions();
        var clock = new Clock(_now);
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), clock);
        var publication = new PlaylistMatchPublication(_factory, queue, _service, clock);
        Assert.Equal(1, await publication.QueueAsync());
        Assert.Equal(0, await publication.QueueAsync());
        if (manualAfterQueue)
        {
            await using var db = _factory.CreateDbContext();
            db.ManualTrackOverrides.Add(Override(old.ExternalSnapshotId, ManualOverrideDecision.Reject, null));
            await db.SaveChangesAsync();
        }
        await using (var db = _factory.CreateDbContext())
        {
            if (change == "account")
            {
                var account = Guid.CreateVersion7();
                db.ProviderAccounts.Add(new()
                {
                    Id = account,
                    OwnerUserId = _user,
                    ProviderId = "fixture",
                    DisplayName = "Replacement account",
                    Enabled = true,
                    CreatedAt = _now,
                    UpdatedAt = _now
                });
                (await db.PlaylistLinks.SingleAsync()).ProviderAccountId = account;
            }

            if (change == "link") (await db.PlaylistLinks.SingleAsync()).Revision++;
            if (change == "disabled") (await db.Users.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        }
        if (change == "source")
        {
            _source.Snapshot = Snapshot("replacement-generation", Entry(0, "entry-1", "source-1", "One updated"));
            await _service.RefreshAsync(execution, _link);
        }
        int decisionCount;
        await using (var before = _factory.CreateDbContext()) decisionCount = await before.TrackMatches.CountAsync();
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("publication-worker"));
        Assert.Equal(TrackRematchScopes.Publication, claim.Payload.GetProperty("Scope").GetString());
        var completion = await publication.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        Assert.Equal(change == "disabled" ? DurableJobCompletionKind.Cancelled : DurableJobCompletionKind.Succeeded, completion.Kind);
        await queue.CompleteAsync(claim, completion);
        await using var saved = _factory.CreateDbContext();
        Assert.Equal(change == "none" ? accepted.Id : old.Id,
            (await saved.PlaylistSourceEntries.SingleAsync(item => item.Id == entryId)).PublishedTrackMatchId);
        Assert.Equal(decisionCount, await saved.TrackMatches.CountAsync());
        Assert.Equal(manualAfterQueue ? 1 : 0, await saved.ManualTrackOverrides.CountAsync());
        Assert.Equal(0, _target.WriteCalls);
        Assert.Equal(0, await publication.QueueAsync());
        if (change == "link")
        {
            var fresh = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("fresh-publication"));
            Assert.Equal(DurableJobCompletionKind.Succeeded,
                (await publication.ExecuteAsync(new(fresh, EmptyServices.Instance), default)).Kind);
            await using var current = _factory.CreateDbContext();
            Assert.Equal(accepted.Id, (await current.PlaylistSourceEntries.SingleAsync()).PublishedTrackMatchId);
        }
    }
}
