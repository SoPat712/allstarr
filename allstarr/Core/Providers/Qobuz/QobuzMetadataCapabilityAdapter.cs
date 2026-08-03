using allstarr.Services;

namespace allstarr.Core.Providers.Qobuz;

public sealed class QobuzMetadataCapabilityAdapter(IConcreteMetadataService legacy)
    : ConcreteMetadataCapabilityAdapter(QobuzDownloadCapabilityAdapter.StableProviderId, legacy);
