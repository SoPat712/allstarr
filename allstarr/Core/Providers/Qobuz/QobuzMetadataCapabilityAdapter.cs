using allstarr.Services;

namespace allstarr.Core.Providers.Qobuz;

public sealed class QobuzMetadataCapabilityAdapter(IConcreteMetadataService legacy)
    : ConcreteMetadataCapabilityAdapter(QobuzDownloadCapabilityAdapter.StableProviderId, legacy);

public sealed class QobuzPlaylistCapabilityAdapter(
    IConcreteMetadataService legacy,
    QobuzMetadataCapabilityAdapter metadata)
    : ConcretePlaylistCapabilityAdapter(QobuzDownloadCapabilityAdapter.StableProviderId, legacy, metadata);
