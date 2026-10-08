namespace allstarr.Core.Capabilities;

public enum ProviderAnalysisState
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record ProviderAnalysisProgress(
    string JobId,
    ProviderAnalysisState State,
    int Completed,
    int Total,
    string? SafeCode = null);

public sealed record ProviderIntelligenceTrack(
    string TrackId,
    string Title,
    string Artist,
    double Score,
    string? Album = null,
    string? ClusterId = null,
    string? Explanation = null);

public sealed record ProviderIntelligenceCluster(
    string Id,
    string Name,
    IReadOnlyList<ProviderIntelligenceTrack> Tracks);

public sealed record ProviderIntelligencePath(
    IReadOnlyList<ProviderIntelligenceTrack> Tracks,
    double TotalDistance);

public sealed record ProviderIntelligenceMapPoint(
    string TrackId,
    string Title,
    string Artist,
    double X,
    double Y,
    string? Album = null,
    string? ClusterId = null);

public sealed record ProviderIntelligenceMapPage(
    IReadOnlyList<ProviderIntelligenceMapPoint> Items,
    string Projection,
    string? NextCursor = null,
    bool IsPartial = false,
    string? SnapshotVersion = null);

public interface IProviderIntelligenceCapability : IProviderCapability
{
    Task<ProviderOutcome<ProviderAnalysisProgress>> StartAnalysisAsync(
        ProviderExecutionContext context, bool rebuild = false);

    Task<ProviderOutcome<ProviderAnalysisProgress>> GetAnalysisProgressAsync(
        ProviderExecutionContext context, string jobId);

    Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>> GetClustersAsync(
        ProviderExecutionContext context, int limit = 50);

    Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> RecommendAsync(
        ProviderExecutionContext context, IReadOnlyList<string> seedTrackIds, int limit);

    Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> SearchAsync(
        ProviderExecutionContext context, string query, bool includeLyrics, int limit);

    Task<ProviderOutcome<ProviderIntelligencePath>> FindPathAsync(
        ProviderExecutionContext context, string startTrackId, string endTrackId, int limit);

    Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> BlendAsync(
        ProviderExecutionContext context, IReadOnlyList<string> positiveSeedTrackIds,
        IReadOnlyList<string> negativeSeedTrackIds, int limit);

    Task<ProviderOutcome<ProviderIntelligenceMapPage>> GetMapAsync(
        ProviderExecutionContext context, ProviderPageRequest page);

    Task<ProviderOutcome<bool>> DisconnectAsync(ProviderExecutionContext context);
}
