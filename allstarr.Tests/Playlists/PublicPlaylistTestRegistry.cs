using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Moq;

namespace allstarr.Tests;

internal static class PublicPlaylistTestRegistry
{
    public static ProviderRegistry Create(params string[] providers) => new(providers.Select(provider =>
    {
        var capability = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns(provider);
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        return Registration(provider, capability.Object);
    }));

    public static ProviderRegistration Registration(string provider, IProviderPlaylistCapability capability) => new(
        new(provider, provider, "Public playlist fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.Optional, "1", ["getUserPlaylists", "getPlaylistTracks"], [ProviderAccountScope.Personal, ProviderAccountScope.Shared], supportsPublicRead: true)], new()),
        [capability]);
}
