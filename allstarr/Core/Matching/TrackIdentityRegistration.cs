using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Matching;

public static class TrackIdentityRegistration
{
    public static IServiceCollection AddTrackIdentity(this IServiceCollection services)
    {
        services.TryAddSingleton<ITrackIdentityService, TrackIdentityService>();
        services.TryAddSingleton<ILibraryIndexService, LibraryIndexService>();
        services.TryAddSingleton<TrackMatchDecisionEngine>();
        services.TryAddSingleton<Playlists.ITrackMatchPersistenceService, Playlists.TrackMatchPersistenceService>();
        services.TryAddSingleton<Playlists.IPlaylistPersistenceService, Playlists.PlaylistPersistenceService>();
        return services;
    }
}
