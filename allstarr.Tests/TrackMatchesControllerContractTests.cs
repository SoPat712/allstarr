using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;

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
              { "providerTrackIds": {}, "title": "Metadata only" }
            ]
            """;
        var parse = typeof(TrackMatchesController).GetMethod(
            "ParseCandidates",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var projected = JsonSerializer.Serialize(parse.Invoke(null, [candidates]));

        Assert.Contains("Local", projected);
        Assert.Contains("External", projected);
        Assert.DoesNotContain("Metadata only", projected);
    }
}
