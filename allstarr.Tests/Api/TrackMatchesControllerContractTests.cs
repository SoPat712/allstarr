using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Storage;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class TrackMatchesControllerContractTests
{
    [Fact]
    public void Candidate_projection_keeps_all_credible_playable_results()
    {
        var localId = Guid.NewGuid();
        var candidates =
            $$"""
            [
              { "libraryTrackId": "weak", "providerTrackIds": { "qobuz": "weak" }, "title": "Wrong", "components": { "title": 1, "artist": 0.15 } },
              { "libraryTrackId": "{{localId}}", "title": "Local" },
              { "libraryTrackId": "legacy-external", "providerTrackIds": { "qobuz": "external" }, "title": "External" },
              { "libraryTrackId": "external-2", "providerTrackIds": { "qobuz": "external-2" }, "title": "External 2" },
              { "libraryTrackId": "external-3", "providerTrackIds": { "qobuz": "external-3" }, "title": "External 3" },
              { "libraryTrackId": "external-4", "providerTrackIds": { "qobuz": "external-4" }, "title": "External 4" },
              { "libraryTrackId": "external-5", "providerTrackIds": { "qobuz": "external-5" }, "title": "External 5" },
              { "libraryTrackId": "legacy-metadata", "providerTrackIds": { "musicbrainzalbum": "release" }, "title": "MusicBrainz album" },
              { "providerTrackIds": {}, "title": "Metadata only" }
            ]
            """;
        var parse = typeof(TrackMatchesController).GetMethod(
            "ParseCandidates",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var projected = JsonSerializer.Serialize(parse.Invoke(
            null,
            [candidates, new HashSet<string>(["qobuz"]), new HashSet<Guid>([localId])]));

        Assert.Contains("Local", projected);
        Assert.Contains("External", projected);
        Assert.Contains("\"isLocal\":true", projected);
        Assert.Contains("\"isLocal\":false", projected);
        Assert.DoesNotContain("MusicBrainz album", projected);
        Assert.DoesNotContain("Metadata only", projected);
        Assert.DoesNotContain("Wrong", projected);
        Assert.Equal(6, JsonDocument.Parse(projected).RootElement.GetArrayLength());
    }

    [Fact]
    public void Provider_search_candidates_are_not_scored_as_local()
    {
        var convert = typeof(TrackMatchesController).GetMethod(
            "ToCandidate",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Song), typeof(Guid), typeof(Guid), typeof(string)])!;
        var candidate = convert.Invoke(null, [
            new Song { Id = "ext-qobuz-track", ExternalProvider = "qobuz", ExternalId = "track" },
            Guid.NewGuid(),
            Guid.NewGuid(),
            "music"
        ])!;

        Assert.False((bool)candidate.GetType().GetProperty("IsLocal")!.GetValue(candidate)!);
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
        Assert.False(Filter(TrackMatchState.Rejected, "attention"));
        Assert.False(Filter(TrackMatchState.Unresolved, "attention"));
        Assert.False(Filter(TrackMatchState.Accepted, "attention"));
        Assert.True(Filter(TrackMatchState.Unresolved, "unresolved"));
        Assert.True(Filter(TrackMatchState.Accepted, "matched"));
        Assert.True(Filter(TrackMatchState.Pinned, "history"));
        Assert.True(Filter(TrackMatchState.Rejected, "history"));
    }
}
