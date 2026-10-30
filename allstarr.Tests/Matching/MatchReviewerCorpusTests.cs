using allstarr.Core.Matching;
using allstarr.Core.Capabilities;

namespace allstarr.Tests;

public sealed class MatchReviewerCorpusTests
{
    [Theory]
    [InlineData(ProviderExplicitContentPolicy.Allow, 1)]
    [InlineData(ProviderExplicitContentPolicy.PreferClean, 0)]
    [InlineData(ProviderExplicitContentPolicy.CleanOnly, 0)]
    public void MessyFollowsContentPolicy(ProviderExplicitContentPolicy policy, int expected)
    {
        var item = MatchReplayTests.LoadCorpus().Single(item => item.Name == "messy-original-over-clean");
        var candidates = item.Candidates.Select((song, index) => song.Candidate(index)).ToArray();
        var engine = new TrackMatchDecisionEngine(new TrackMatchPolicy { ExplicitContent = policy });
        var result = engine.Decide(MatchReplayTests.Scope(), item.Source.Snapshot(), candidates, providerPriority: item.Priority);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(candidates[expected].LibraryTrackId, result.SelectedLibraryTrackId);
        if (policy == ProviderExplicitContentPolicy.CleanOnly)
            Assert.DoesNotContain(result.Candidates, candidate => candidate.IsExplicit != false);
    }

    public static IEnumerable<object[]> Cases() => MatchReplayTests.LoadCorpus().Select(item => new object[] { item.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProductionFollowsReviewerRules(string name)
    {
        var item = MatchReplayTests.LoadCorpus().Single(item => item.Name == name);
        var candidates = item.Candidates.Select((song, index) => song.Candidate(index)).ToArray();
        var decision = new TrackMatchDecisionEngine().Decide(MatchReplayTests.Scope(), item.Source.Snapshot(), candidates,
            providerPriority: item.Priority);
        Assert.Equal(item.ExpectedState, decision.State.ToString());
        Assert.Equal(item.ExpectedCandidate.HasValue ? candidates[item.ExpectedCandidate.Value].LibraryTrackId : (Guid?)null,
            decision.SelectedLibraryTrackId);
    }
}
