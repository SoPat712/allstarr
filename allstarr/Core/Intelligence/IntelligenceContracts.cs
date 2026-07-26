namespace allstarr.Core.Intelligence;

public sealed record IntelligenceScope(Guid TenantId, Guid OwnerUserId, string Protocol,
    string BackendInstanceId, string LibraryScopeId);

public sealed record RecommendationSignal(string Code, double Weight, string Explanation);
public sealed record RecommendationTrackIdentity(string? ProviderId = null, string? ProviderTrackId = null,
    string? MusicBrainzRecordingId = null, string? Isrc = null, string? Title = null,
    string? Artist = null, string? Album = null, Guid? LibraryTrackId = null, string? BackendItemId = null);
public sealed record RecommendationCandidate(string TrackKey, double Score, string Source,
    IReadOnlyList<RecommendationSignal> Signals, RecommendationTrackIdentity? Identity = null);
public sealed record ListeningProfile(Guid TenantId, Guid OwnerUserId, string BackendInstanceId,
    string LibraryScopeId, int PlayCount, int SkipCount, int FavoriteCount,
    IReadOnlyDictionary<string, double> TopGenres, DateTimeOffset WindowStart, DateTimeOffset WindowEnd)
{
    // Exact-scope internal library references, never provider credentials.
    public IReadOnlyList<string> TopTrackKeys { get; init; } = [];
}
public sealed record RecommendationRequest(IntelligenceScope Scope, Guid RunId,
    ListeningProfile Profile, IReadOnlyList<string> SeedTrackKeys, int Limit, string IdempotencyKey,
    bool ExplicitlyOptedIn, CancellationToken CancellationToken);
public enum RecommendationProviderState { Succeeded, Disabled, Degraded, Unauthorized, Unsupported }
public sealed record RecommendationProviderResult(RecommendationProviderState State,
    IReadOnlyList<RecommendationCandidate> Candidates, string? SafeErrorCode = null);
// Internal post-SDK-v1 seams. These interfaces are deliberately not extension capabilities.
public interface IRecommendationProvider
{
    string Id { get; }
    Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request);
}
public enum RecommendationProviderReadinessState { Ready, Disabled, Degraded, Unauthorized, Unconfigured, Unsupported }
public sealed record RecommendationProviderReadiness(string ProviderId,
    RecommendationProviderReadinessState State, string? SafeReasonCode = null,
    int? EligibleLocalTrackCount = null);
public interface IRecommendationProviderReadiness
{
    Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken cancellationToken = default);
}
public interface IRecommendationProviderStatusService
{
    Task<IReadOnlyList<RecommendationProviderReadiness>> ListAsync(IntelligenceScope scope, CancellationToken cancellationToken = default);
}
public interface IListeningProfileService
{
    Task<ListeningProfile> BuildAsync(IntelligenceScope scope, CancellationToken cancellationToken = default);
}
public interface ISmartPlaylistService
{
    Task<Guid> CreateGeneratedSetAsync(IntelligenceScope scope, Guid runId, string name,
        IReadOnlyList<RecommendationCandidate> candidates, CancellationToken cancellationToken = default);
}
public sealed record GeneratedSetMaterializationRequest(IntelligenceScope Scope, Guid GeneratedSetId,
    IReadOnlyList<RecommendationCandidate> OrderedCandidates, string IdempotencyKey);
public sealed record GeneratedSetMaterializationResult(bool Succeeded, bool Retryable = false,
    string? SafeErrorCode = null, string? BackendPlaylistId = null, string? TargetRevision = null);
public interface IGeneratedSetMaterializer
{
    string Protocol { get; }
    Task<GeneratedSetMaterializationResult> MaterializeAsync(GeneratedSetMaterializationRequest request,
        CancellationToken cancellationToken);
}
public interface IRecommendationSignalWriter
{
    Task<bool> WriteAsync(IntelligenceScope scope, string signalType, string trackKey,
        double value, DateTimeOffset observedAt, CancellationToken cancellationToken = default);
}
public interface IIdempotentRecommendationSignalWriter : IRecommendationSignalWriter
{
    Task<bool> WriteIdempotentAsync(IntelligenceScope scope, string signalType, string trackKey, double value,
        DateTimeOffset observedAt, string signalKey, Guid sourceJobId, CancellationToken cancellationToken = default);
}
