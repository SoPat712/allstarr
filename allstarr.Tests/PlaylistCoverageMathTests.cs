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
}
