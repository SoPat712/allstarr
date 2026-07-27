using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Services.Admin;

namespace allstarr.Tests;

public sealed class PlaylistCoverageMathTests
{
    [Fact]
    public void Normalize_PreventsProviderRoutesFromInflatingSourceCoverage()
    {
        var result = PlaylistCoverageMath.Normalize(
            trackCount: 47,
            local: 85,
            external: 0,
            missing: -38);

        Assert.Equal(47, result.Total);
        Assert.Equal(47, result.Local);
        Assert.Equal(0, result.External);
        Assert.Equal(0, result.Missing);
        Assert.Equal(47, result.Playable);
    }

    [Fact]
    public void Normalize_PreservesValidCanonicalCoverage()
    {
        var result = PlaylistCoverageMath.Normalize(
            trackCount: 47,
            local: 43,
            external: 1,
            missing: 3);

        Assert.Equal(new PlaylistCoverageCounts(47, 43, 1, 3), result);
    }

    [Fact]
    public void Normalize_AssignsRemainingPositionsToMissing()
    {
        var result = PlaylistCoverageMath.Normalize(
            trackCount: 47,
            local: -2,
            external: -4,
            missing: 99);

        Assert.Equal(new PlaylistCoverageCounts(47, 0, 0, 47), result);
    }

    [Fact]
    public void Normalize_CapsExternalCoverageAfterLocalCoverage()
    {
        var result = PlaylistCoverageMath.Normalize(
            trackCount: 47,
            local: 43,
            external: 20,
            missing: 0);

        Assert.Equal(new PlaylistCoverageCounts(47, 43, 4, 0), result);
    }

    [Fact]
    public void DurableProjection_OwnsConsistentListAndDetailCounts()
    {
        var projection = new DurablePlaylistProjection(
            Guid.NewGuid(), Guid.NewGuid(), 1, "Playlist", "source", "playlist",
            Guid.NewGuid(), "jellyfin", null, null, DateTimeOffset.UtcNow, null, null,
            [
                Entry("local", TrackMatchState.Accepted, "backend"),
                Entry("external", TrackMatchState.Suggested, routes: [new("provider", "track")]),
                Entry("unmatched", TrackMatchState.Rejected)
            ]);

        Assert.Equal(projection.TotalCount,
            projection.LocalCount + projection.ExternalCount + projection.MissingCount);
        Assert.Equal(2, projection.PlayableCount);
        Assert.Equal(1, projection.MatchedCount);
        Assert.Equal(1, projection.ReviewCount);
        Assert.Equal(1, projection.RejectedCount);
    }

    private static DurablePlaylistEntryProjection Entry(
        string route,
        TrackMatchState state,
        string? backend = null,
        IReadOnlyList<DurableProviderRoute>? routes = null) =>
        new(0, Guid.NewGuid(), "external", "Track", [], null, null, null, null, null,
            state, backend, route, routes?.FirstOrDefault()?.ProviderId, routes ?? []);
}
