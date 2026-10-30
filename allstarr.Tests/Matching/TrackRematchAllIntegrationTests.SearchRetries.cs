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
}
