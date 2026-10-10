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
        services.TryAddSingleton<TrackMatchCommandService>();
        services.TryAddSingleton<PlaylistRematchService>();
        services.TryAddSingleton<TrackRematchAllService>();
        services.TryAddSingleton<ITrackMatchRepository>(provider =>
            provider.GetRequiredService<TrackMatchCommandService>());
        services.TryAddSingleton<PlaylistRematchJobHandler>();
        services.TryAddSingleton<TrackRematchAllJobHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableJobHandler, TrackRematchJobHandler>());
        services.AddHostedService<TrackMatchAlgorithmRolloutService>();
        services.TryAddSingleton<Playlists.IPlaylistPersistenceService, Playlists.PlaylistPersistenceService>();
        return services;
    }
}
