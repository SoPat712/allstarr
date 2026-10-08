using allstarr.Core.Capabilities;

namespace allstarr.Core.Intelligence;

public sealed record RecommendationSourceItem(string TrackKey, double Score,
    IReadOnlyList<RecommendationSignal> Signals, RecommendationTrackIdentity? Identity = null,
    Guid? ProviderAccountId = null, string? SourceRevision = null);

public sealed record ScopedRecommendationQuery(IntelligenceScope Scope, ListeningProfile Profile,
    IReadOnlyList<string> SeedTrackKeys, int Limit);

public sealed record AudioMusePathResult(
    IReadOnlyList<RecommendationSourceItem> Tracks,
    double TotalDistance);

public sealed record AudioMuseMapPoint(
    RecommendationTrackIdentity Identity,
    double X,
    double Y,
    string? ClusterId = null);

public sealed record AudioMuseMapPage(
    IReadOnlyList<AudioMuseMapPoint> Items,
    string Projection,
    string? NextCursor = null,
    bool IsPartial = false,
    string? SnapshotVersion = null);

public sealed record AudioMuseCluster(
    string Id,
    string Name,
    IReadOnlyList<RecommendationSourceItem> Tracks);

public enum ListenBrainzDiscoveryKind
{
    CollaborativeFiltering,
    WeeklyExploration,
    WeeklyJams,
    TopRecordings
}

public interface IJellyfinInstantMixClient
{
    Task<IReadOnlyList<RecommendationSourceItem>> GetInstantMixAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken);
}
public interface ILastFmRecommendationClient
{
    bool IsConfigured { get; }
    Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecommendationSourceItem>> GetSimilarTracksAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken);
}
public interface IListenBrainzRecommendationClient
{
    bool IsConfigured { get; }
    Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecommendationSourceItem>> GetRecommendationsAsync(ScopedRecommendationQuery query,
        ListenBrainzDiscoveryKind kind, CancellationToken cancellationToken);
}
public interface IAudioMuseRecommendationClient
{
    bool IsAvailable { get; }
    Task<bool> CheckHealthAsync(IntelligenceScope scope, CancellationToken cancellationToken);
    async Task<RecommendationProviderReadiness> GetReadinessAsync(
        IntelligenceScope scope, CancellationToken cancellationToken) =>
        !IsAvailable
            ? new("audiomuse-ai", RecommendationProviderReadinessState.Unconfigured,
                "audiomuse_unavailable")
            : await CheckHealthAsync(scope, cancellationToken)
                ? new("audiomuse-ai", RecommendationProviderReadinessState.Ready)
                : new("audiomuse-ai", RecommendationProviderReadinessState.Degraded,
                    "audiomuse_unhealthy");
    Task<IReadOnlyList<RecommendationSourceItem>> RecommendAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecommendationSourceItem>> FindSimilarAsync(IntelligenceScope scope,
        IReadOnlyList<string> seedTrackIds, int limit, CancellationToken cancellationToken);
    Task<ProviderAnalysisProgress> StartAnalysisAsync(IntelligenceScope scope, bool rebuild,
        string idempotencyKey, CancellationToken cancellationToken);
    Task<ProviderAnalysisProgress> GetAnalysisProgressAsync(IntelligenceScope scope, string jobId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AudioMuseCluster>> GetClustersAsync(IntelligenceScope scope, int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<RecommendationSourceItem>> SearchAsync(IntelligenceScope scope, string query,
        bool includeLyrics, int limit, CancellationToken cancellationToken);
    Task<AudioMusePathResult> FindPathAsync(IntelligenceScope scope, string startTrackId,
        string endTrackId, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecommendationSourceItem>> BlendAsync(IntelligenceScope scope,
        IReadOnlyList<string> positiveSeedTrackIds, IReadOnlyList<string> negativeSeedTrackIds,
        int limit, CancellationToken cancellationToken);
    Task<AudioMuseMapPage> GetMapAsync(IntelligenceScope scope, ProviderPageRequest page,
        CancellationToken cancellationToken);
}
public interface ILocalRecommendationCatalog
{
    Task<bool> HasCoverageAsync(IntelligenceScope scope, bool requireMusicBrainz, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecommendationSourceItem>> FindRelatedAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ResolveTrackKeysAsync(IntelligenceScope scope,
        IReadOnlyList<string> trackKeys, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, RecommendationTrackIdentity>> ResolveBackendItemsAsync(
        IntelligenceScope scope, IReadOnlyList<string> backendItemIds, CancellationToken cancellationToken);
}

public abstract class BoundedRecommendationProvider(string id) : IRecommendationProvider, IRecommendationProviderReadiness
{
    public string Id { get; } = id;
    protected abstract bool Available { get; }
    protected abstract string UnavailableCode { get; }
    protected abstract Task<IReadOnlyList<RecommendationSourceItem>> FetchAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken);
    public abstract Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken cancellationToken = default);
    protected virtual TimeSpan RequestTimeout => TimeSpan.FromSeconds(10);

    public async Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request)
    {
        Validate(request);
        if (!request.ExplicitlyOptedIn)
            return new(RecommendationProviderState.Disabled, [], "recommendation_opt_in_required");
        if (!Available)
            return new(RecommendationProviderState.Unsupported, [], UnavailableCode);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
            timeout.CancelAfter(RequestTimeout);
            var seeds = request.SeedTrackKeys.Concat(request.Profile.TopTrackKeys).Distinct(StringComparer.Ordinal).Take(100).ToArray();
            var items = await FetchAsync(new(request.Scope, request.Profile, seeds, request.Limit), timeout.Token)
                .WaitAsync(timeout.Token);
            var candidates = items.Take(request.Limit).Select(item => new RecommendationCandidate(
                Required(item.TrackKey, 500), Math.Clamp(item.Score, 0, 1), Id,
                item.Signals.Select(signal => new RecommendationSignal(Required(signal.Code, 100),
                    Math.Clamp(signal.Weight, 0, 1), Required(signal.Explanation, 500))).ToArray(), item.Identity)
            { ProviderAccountId = item.ProviderAccountId, SourceRevision = item.SourceRevision }).ToArray();
            return new(RecommendationProviderState.Succeeded, candidates, null);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested) { throw; }
        catch (NotSupportedException)
        {
            return new(RecommendationProviderState.Unsupported, [], UnavailableCode);
        }
        catch (OperationCanceledException)
        {
            return new(RecommendationProviderState.Degraded, [], $"{Id}_request_timed_out");
        }
        catch (UnauthorizedAccessException)
        {
            return new(RecommendationProviderState.Unauthorized, [], $"{Id}_account_unauthorized");
        }
        catch (Exception)
        {
            return new(RecommendationProviderState.Degraded, [], $"{Id}_temporarily_unavailable");
        }
    }

    private static void Validate(RecommendationRequest request)
    {
        var scope = request.Scope;
        if (scope.TenantId == Guid.Empty || scope.OwnerUserId == Guid.Empty || request.RunId == Guid.Empty ||
            string.IsNullOrWhiteSpace(scope.Protocol) || string.IsNullOrWhiteSpace(scope.BackendInstanceId) ||
            string.IsNullOrWhiteSpace(scope.LibraryScopeId) || request.Limit is < 1 or > 200 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 300 ||
            request.Profile.TenantId != scope.TenantId || request.Profile.OwnerUserId != scope.OwnerUserId ||
            request.Profile.BackendInstanceId != scope.BackendInstanceId || request.Profile.LibraryScopeId != scope.LibraryScopeId)
            throw new ArgumentException("Recommendation request scope is invalid.", nameof(request));
    }

    private static string Required(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidOperationException("Recommendation source returned malformed data.");
        return value;
    }
}

public sealed class JellyfinInstantMixRecommendationProvider(IJellyfinInstantMixClient client)
    : BoundedRecommendationProvider("jellyfin-instant-mix")
{
    protected override bool Available => true;
    protected override string UnavailableCode => "jellyfin_instant_mix_unsupported";
    public override Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token = default) => Task.FromResult(scope.Protocol == "jellyfin" ? new RecommendationProviderReadiness(Id, RecommendationProviderReadinessState.Ready) : new(Id, RecommendationProviderReadinessState.Unsupported, "jellyfin_instant_mix_wrong_protocol"));
    protected override Task<IReadOnlyList<RecommendationSourceItem>> FetchAsync(ScopedRecommendationQuery query, CancellationToken token) => client.GetInstantMixAsync(query, token);
}
public sealed class LastFmRecommendationProvider(ILastFmRecommendationClient client)
    : BoundedRecommendationProvider("lastfm")
{
    protected override bool Available => client.IsConfigured;
    protected override string UnavailableCode => "lastfm_recommendations_not_configured";
    public override async Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token = default) => (await client.GetReadinessAsync(scope, token)) with { ProviderId = Id };
    protected override Task<IReadOnlyList<RecommendationSourceItem>> FetchAsync(ScopedRecommendationQuery query, CancellationToken token) => client.GetSimilarTracksAsync(query, token);
}
public sealed class ListenBrainzRecommendationProvider(
    IListenBrainzRecommendationClient client,
    ListenBrainzDiscoveryKind kind = ListenBrainzDiscoveryKind.CollaborativeFiltering,
    string id = "listenbrainz") : BoundedRecommendationProvider(id)
{
    protected override bool Available => client.IsConfigured;
    protected override string UnavailableCode => "listenbrainz_recommendations_not_configured";
    public override async Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token = default) => (await client.GetReadinessAsync(scope, token)) with { ProviderId = Id };
    protected override Task<IReadOnlyList<RecommendationSourceItem>> FetchAsync(ScopedRecommendationQuery query, CancellationToken token) =>
        client.GetRecommendationsAsync(query, kind, token);
}
public sealed class AudioMuseRecommendationProvider(IAudioMuseRecommendationClient client)
    : BoundedRecommendationProvider("audiomuse-ai")
{
    protected override bool Available => client.IsAvailable;
    protected override string UnavailableCode => "audiomuse_unavailable";
    public override async Task<RecommendationProviderReadiness> GetReadinessAsync(
        IntelligenceScope scope, CancellationToken token = default) =>
        (await client.GetReadinessAsync(scope, token)) with { ProviderId = Id };
    protected override Task<IReadOnlyList<RecommendationSourceItem>> FetchAsync(ScopedRecommendationQuery query, CancellationToken token) => client.RecommendAsync(query, token);
}
public sealed class LocalRuleRecommendationProvider(ILocalRecommendationCatalog catalog)
    : BoundedRecommendationProvider("local-rules")
{
    protected override bool Available => true;
    protected override string UnavailableCode => "local_rules_unavailable";
    public override async Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token = default) => await catalog.HasCoverageAsync(scope, false, token) ? new(Id, RecommendationProviderReadinessState.Ready) : new(Id, RecommendationProviderReadinessState.Degraded, "local_catalog_empty");
    protected override Task<IReadOnlyList<RecommendationSourceItem>> FetchAsync(ScopedRecommendationQuery query, CancellationToken token) => catalog.FindRelatedAsync(query, token);
}
