using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackRematchAllIntegrationTests
{
    [Theory]
    [InlineData(0, 0, 500, false, false)]
    [InlineData(24, 0, 25, true, false)]
    [InlineData(0, 10000, 10001, true, false)]
    [InlineData(0, 0, 1, false, true)]
    [Trait("Category", "Sqlite")]
    public async Task AlgorithmRolloutUsesCompletedSearchesWhenItsRetryBudgetIsExhausted(
        int failures, int deferrals, int attempt, bool exhausted, bool preferredAccepted)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var now = DateTimeOffset.UtcNow;
        var clock = new Clock(now);
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        await using (var setup = factory.CreateDbContext())
        {
            setup.Users.Add(User(owner, "Owner", now));
            setup.ProviderAccounts.Add(Account(account, owner, now));
            setup.ExternalMetadataSnapshots.Add(Snapshot(snapshot, account, owner, "Track", Hash(1000), now));
            setup.TrackMatches.Add(Decision(snapshot, owner, null, null, TrackMatchState.Unresolved, now));
            await setup.SaveChangesAsync();
        }
        var rematches = new TrackRematchAllService(factory, Queue(factory, clock), clock);
        Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());
        DurableJobRecord job;
        await using (var queued = factory.CreateDbContext())
        {
            job = await queued.Jobs.SingleAsync();
            job.FailureCount = failures;
            job.DeferralCount = deferrals;
            await queued.SaveChangesAsync();
        }
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(preferredAccepted
                ? new MatchingSongSearchResult([new Song { ExternalProvider = "apple-download", ExternalId = "selected", Title = "Track", Artist = "Artist", Duration = 180 }], ["deezer"])
                : new MatchingSongSearchResult([], ["apple-download"]));
        var commands = new TrackMatchCommandService(factory, new(), new ProviderAccountResolver(factory), clock,
            new TestBackendLibraryAccess(factory, "music"),
            new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance));
        var handler = new TrackRematchAllJobHandler(factory, rematches, commands, clock);
        var claim = new DurableJobClaim(job.Id, attempt, job.Type, JsonDocument.Parse(job.PayloadJson).RootElement.Clone(),
            owner, null, null, JsonSerializer.SerializeToElement(new { }), job.CorrelationId);

        var completion = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);

        var completed = exhausted || preferredAccepted;
        Assert.Equal(completed ? DurableJobCompletionKind.Succeeded : DurableJobCompletionKind.Retry, completion.Kind);
        await using var saved = factory.CreateDbContext();
        var decisions = await saved.TrackMatches.OrderBy(item => item.DecisionVersion).ToArrayAsync();
        Assert.Equal(completed ? 2 : 1, decisions.Length);
        Assert.Equal(preferredAccepted ? TrackMatchState.Accepted : TrackMatchState.Unresolved, decisions[^1].State);
        Assert.Equal(completed ? TrackMatchDecisionEngine.AlgorithmVersion : "retired-v1", decisions[^1].MatcherVersion);
        if (completed)
        {
            Assert.Equal(exhausted ? TrackRematchAllService.RolloutFallbackPolicyVersion : TrackRematchAllService.RolloutPolicyVersion,
                decisions[^1].PolicyVersion);
            await using var finish = factory.CreateDbContext();
            (await finish.Jobs.SingleAsync()).State = DurableJobState.Succeeded;
            await finish.SaveChangesAsync();
            Assert.Equal(0, await rematches.QueueAlgorithmUpgradesAsync());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("Category", "Sqlite")]
    public async Task ARematchThatFindsNoServiceCopyKeepsTheStrongerMatch(bool incomplete, bool found)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var now = DateTimeOffset.UtcNow;
        var clock = new Clock(now);
        var owner = Guid.CreateVersion7();
        var account = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        await using (var setup = factory.CreateDbContext())
        {
            setup.Users.Add(User(owner, "Owner", now));
            setup.ProviderAccounts.Add(Account(account, owner, now));
            setup.ExternalMetadataSnapshots.Add(Snapshot(snapshot, account, owner, "Track", Hash(1001), now));
            var held = Decision(snapshot, owner, null, null, TrackMatchState.Ambiguous, now);
            held.Confidence = .992;
            held.CandidateResultsJson = JsonSerializer.Serialize(new[]
            {
                new { IsLocal = false, Title = "Track", ProviderTrackIds = new Dictionary<string, string> { ["deezer"] = "held" } }
            });
            setup.TrackMatches.Add(held);
            await setup.SaveChangesAsync();
        }
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult(
                found ? [new Song { ExternalProvider = "deezer", ExternalId = "again", Title = "Track", Artist = "Artist", Duration = 180 }] : [],
                incomplete ? ["deezer"] : []));
        var commands = new TrackMatchCommandService(factory, new(), new ProviderAccountResolver(factory), clock,
            new TestBackendLibraryAccess(factory, "music"),
            new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance));
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", owner.ToString("N"),
            new AllstarrPrincipal(owner, "jellyfin", "backend", owner.ToString("N"), "Owner", true),
            "kept-match", now.AddMinutes(2), default);

        var result = await commands.RematchSnapshotAsync(execution, snapshot, "kept-match", "manual-rematch-v3");

        await using var saved = factory.CreateDbContext();
        var decisions = await saved.TrackMatches.OrderBy(item => item.DecisionVersion).ToArrayAsync();
        if (found)
        {
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(2, decisions.Length);
            return;
        }
        Assert.False(result.Succeeded);
        Assert.Equal(TrackMatchCommandFailure.ServicesUnavailable, result.Failure);
        Assert.False(result.Lasting);
        var kept = Assert.Single(decisions);
        Assert.Equal(TrackMatchState.Ambiguous, kept.State);
        Assert.Equal(.992, kept.Confidence);
    }
}
