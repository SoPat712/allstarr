using allstarr.Core.Capabilities;
using allstarr.Core.Routing;

namespace allstarr.Core.Migrations;

internal abstract class CollectionMigrationSource(ProviderRouteCandidate<IProviderPlaylistCapability> route)
{
    public IProviderPlaylistCapability Implementation => route.Implementation;
    public ProviderExecutionContext Context => route.Context;
    public string ProviderId => Context.ProviderId;
    public abstract string Name { get; }
    public virtual string ArtistsName => "Followed artists";
    public virtual int MaximumPages => 1000;

    public static CollectionMigrationSource Create(ProviderRouteCandidate<IProviderPlaylistCapability> route) => route.Context.ProviderId switch
    {
        "spotify" => new SpotifySource(route),
        "apple-musickit" => new AppleMusicSource(route),
        _ => throw new UnauthorizedAccessException()
    };

    public static string DisplayName(string providerId) => providerId == "apple-musickit" ? "Apple Music" : "Spotify";

    private sealed class SpotifySource(ProviderRouteCandidate<IProviderPlaylistCapability> route) : CollectionMigrationSource(route)
    {
        public override string Name => "Spotify";
    }

    private sealed class AppleMusicSource(ProviderRouteCandidate<IProviderPlaylistCapability> route) : CollectionMigrationSource(route)
    {
        public override string Name => "Apple Music";
        public override string ArtistsName => "Artists in your library";
        public override int MaximumPages => 4000;
    }
}
