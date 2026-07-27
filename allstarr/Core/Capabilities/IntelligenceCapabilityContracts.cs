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
    string? Path = null,
    string? Explanation = null);

public sealed record ProviderIntelligenceCluster(
    string Id,
    string Name,
    IReadOnlyList<ProviderIntelligenceTrack> Tracks);

public sealed record ProviderPlaylistExportResult(
    string PlaylistId,
    string Revision,
    int TrackCount);

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

    Task<ProviderOutcome<ProviderPlaylistExportResult>> ExportPlaylistAsync(
        ProviderExecutionContext context, string name, IReadOnlyList<string> trackIds);

    Task<ProviderOutcome<bool>> DisconnectAsync(ProviderExecutionContext context);
}
