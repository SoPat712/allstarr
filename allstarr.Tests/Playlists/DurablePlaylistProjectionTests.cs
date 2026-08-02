using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class DurablePlaylistProjectionTests
{
    [Fact]
    public void DurableProjection_OwnsConsistentListAndDetailCounts()
    {
        var projection = new DurablePlaylistProjection(
            Guid.NewGuid(), Guid.NewGuid(), 1, "Playlist", "source", "playlist",
            Guid.NewGuid(), "jellyfin", null, null, DateTimeOffset.UtcNow, null, null,
            [
                Entry("local", TrackMatchState.Accepted, "backend"),
                Entry("external", TrackMatchState.Suggested, routes:
                [
                    new("provider", "track"),
                    new("backup", "track")
                ]),
                Entry("unmatched", TrackMatchState.Rejected)
            ]);

        Assert.Equal(projection.TotalCount,
            projection.LocalCount + projection.ExternalCount + projection.MissingCount);
        Assert.Equal(2, projection.PlayableCount);
        Assert.Equal(1, projection.MatchedCount);
        Assert.Equal(1, projection.ReviewCount);
        Assert.Equal(1, projection.RejectedCount);
        Assert.Equal(1, projection.RouteCounts["jellyfin"]);
        Assert.Equal(1, projection.RouteCounts["provider"]);
        Assert.False(projection.RouteCounts.ContainsKey("backup"));
        Assert.Equal(1, projection.RouteCounts["unresolved"]);
    }

    [Fact]
    public void ProjectionSelector_PreservesLogicalRowsAndRequiresAnExactTarget()
    {
        var source = new[] { Row("source-a"), Row("source-b"), Row("source-c"), Row("source-d") };
        var resolved = new[] { Row("local-a"), Row("external-b", "alternate-b"), Row("local-c"), Row("external-d") };
        var target = new[] { Row("target-a"), Row("target-c") };

        Assert.Same(source, PlaylistProjectionSelector.Select(
            PlaylistProjectionMode.Source, source, resolved));
        var selected = PlaylistProjectionSelector.Select(
            PlaylistProjectionMode.Resolved, source, resolved)!;
        Assert.Same(resolved, selected);
        Assert.Equal(4, selected.Count);
        Assert.Single(selected[1].AlternateRoutes);
        Assert.Null(PlaylistProjectionSelector.Select(
            PlaylistProjectionMode.Target, source, resolved));
        Assert.Same(target, PlaylistProjectionSelector.Select(
            PlaylistProjectionMode.Target, source, resolved, target));
        Assert.Throws<ArgumentException>(() => PlaylistProjectionSelector.Select(
            PlaylistProjectionMode.Resolved, source, resolved[..3]));
    }

    private static ProjectionRow Row(string id, params string[] alternates) => new(id, alternates);
    private sealed record ProjectionRow(string Id, IReadOnlyList<string> AlternateRoutes);

    private static DurablePlaylistEntryProjection Entry(
        string route,
        TrackMatchState state,
        string? backend = null,
        IReadOnlyList<DurableProviderRoute>? routes = null) =>
        new(0, Guid.NewGuid(), "external", "Track", [], null, null, null, null, null,
            state, backend, route, routes?.FirstOrDefault()?.ProviderId, routes ?? []);
}
