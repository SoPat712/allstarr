using allstarr.Core.Matching;

namespace allstarr.Tests;

public sealed class MatchReviewerCorpusTests
{
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
