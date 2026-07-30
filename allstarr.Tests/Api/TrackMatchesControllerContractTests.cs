using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class TrackMatchesControllerContractTests
{
    [Fact]
    public void Candidate_projection_keeps_local_and_provider_only_playable_results()
    {
        const string candidates =
            """
            [
              { "libraryTrackId": "local", "title": "Local" },
              { "providerTrackIds": { "qobuz": "external" }, "title": "External" },
              { "providerTrackIds": { "musicbrainzalbum": "release" }, "title": "MusicBrainz album" },
              { "providerTrackIds": {}, "title": "Metadata only" }
            ]
            """;
        var parse = typeof(TrackMatchesController).GetMethod(
            "ParseCandidates",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var projected = JsonSerializer.Serialize(parse.Invoke(
            null,
            [candidates, new HashSet<string>(["qobuz"])]));

        Assert.Contains("Local", projected);
        Assert.Contains("External", projected);
        Assert.DoesNotContain("MusicBrainz album", projected);
        Assert.DoesNotContain("Metadata only", projected);
    }

    [Fact]
    public void Attention_filter_is_disjoint_from_matched_and_unresolved()
    {
        var matches = typeof(TrackMatchesController).GetMethod(
            "MatchesStateFilter",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Filter(TrackMatchState state, string value) =>
            (bool)matches.Invoke(null, [state, value])!;

        Assert.True(Filter(TrackMatchState.Suggested, "attention"));
        Assert.True(Filter(TrackMatchState.Ambiguous, "attention"));
        Assert.True(Filter(TrackMatchState.Rejected, "attention"));
        Assert.False(Filter(TrackMatchState.Unresolved, "attention"));
        Assert.False(Filter(TrackMatchState.Accepted, "attention"));
        Assert.True(Filter(TrackMatchState.Unresolved, "unresolved"));
        Assert.True(Filter(TrackMatchState.Accepted, "matched"));
    }
}
