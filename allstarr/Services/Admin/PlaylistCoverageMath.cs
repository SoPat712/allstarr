namespace allstarr.Services.Admin;

public readonly record struct PlaylistCoverageCounts(
    int Total,
    int Local,
    int External,
    int Missing)
{
    public int Playable => Local + External;
}

public static class PlaylistCoverageMath
{
    public static PlaylistCoverageCounts Normalize(
        int trackCount,
        int local,
        int external,
        int missing)
    {
        var total = Math.Max(0, trackCount);
        var normalizedLocal = Math.Clamp(local, 0, total);
        var normalizedExternal = Math.Clamp(external, 0, total - normalizedLocal);
        var normalizedMissing = total - normalizedLocal - normalizedExternal;

        return new PlaylistCoverageCounts(
            total,
            normalizedLocal,
            normalizedExternal,
            normalizedMissing);
    }
}
