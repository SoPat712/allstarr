using allstarr.Core.Jobs;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Matching;

public static class TrackIdentityRegistration
{
    public static IServiceCollection AddTrackIdentity(this IServiceCollection services)
    {
        services.TryAddSingleton<ITrackIdentityService, TrackIdentityService>();
        services.TryAddSingleton<ILibraryIndexService, LibraryIndexService>();
        services.TryAddSingleton<TrackMatchDecisionEngine>();
        services.TryAddSingleton<TrackMatchPlayableSearch>();
        services.AddHttpClient(JellyfinMatchLibraryClient.HttpClientName);
        services.TryAddSingleton<IMatchLibraryClient, JellyfinMatchLibraryClient>();
        services.TryAddSingleton<MatchLibrarySearch>();
        services.TryAddSingleton<TrackMatchCommandService>();
        services.TryAddSingleton<PlaylistRematchService>();
        services.TryAddSingleton<PlaylistMatchPublication>();
        services.TryAddSingleton<TrackRematchAllService>();
        services.TryAddSingleton<ITrackMatchRepository>(provider =>
            provider.GetRequiredService<TrackMatchCommandService>());
        services.TryAddSingleton<PlaylistRematchJobHandler>();
        services.TryAddSingleton<TrackRematchAllJobHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableJobHandler, TrackRematchJobHandler>());
        services.AddHostedService<TrackMatchAlgorithmRolloutService>();
        services.TryAddSingleton<CatalogIdentityMaintenance>();
        services.AddHostedService<CatalogIdentityMaintenanceService>();
        services.TryAddSingleton<RecordingMusicBrainzEnrichmentQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableJobHandler, RecordingMusicBrainzEnrichmentJobHandler>());
        services.AddHostedService<RecordingMusicBrainzEnrichmentService>();
        services.AddSingleton<Enrichment.SharedTagStore>();
        services.AddSingleton<Enrichment.SharedTagSources>();
        services.AddSingleton<Enrichment.SharedTagQueue>();
        services.AddHttpClient(nameof(Enrichment.SharedTagSources));
        services.AddSingleton<IDurableJobHandler, Enrichment.SharedTagJobHandler>();
        services.AddHostedService<Enrichment.SharedTagDiscoveryService>();
        services.TryAddSingleton<RecordingOdesliEnrichmentQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableJobHandler, RecordingOdesliEnrichmentJobHandler>());
        services.TryAddSingleton<Playlists.IPlaylistPersistenceService, Playlists.PlaylistPersistenceService>();
        return services;
    }
}
