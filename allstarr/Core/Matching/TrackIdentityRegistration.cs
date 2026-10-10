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
        services.TryAddSingleton<TrackMatchCommandService>();
        services.TryAddSingleton<PlaylistRematchService>();
        services.TryAddSingleton<TrackRematchAllService>();
        services.TryAddSingleton<ITrackMatchRepository>(provider =>
            provider.GetRequiredService<TrackMatchCommandService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableJobHandler, PlaylistRematchJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableJobHandler, TrackRematchAllJobHandler>());
        services.AddHostedService<TrackMatchAlgorithmRolloutService>();
        services.TryAddSingleton<Playlists.IPlaylistPersistenceService, Playlists.PlaylistPersistenceService>();
        return services;
    }
}
