using allstarr.Core.Capabilities;

namespace allstarr.Core.Protocols;

// A song id from a service that can't play songs, such as a ListenBrainz listen or like, names a recording; it reads and
// plays through that recording's other copies.
internal static class RecordingOnlySongs
{
    internal static bool IsRecordingOnly(IProviderRegistry registry, string? providerId) =>
        providerId is { Length: > 0 } and not "allstarr" && registry.TryGet(providerId, out var provider) && provider != null &&
        provider.Capabilities.All(capability => capability.Capability != ProviderCapabilityKind.Streaming);
}
