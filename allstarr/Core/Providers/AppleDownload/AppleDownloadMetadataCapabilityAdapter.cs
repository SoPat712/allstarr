using allstarr.Services;

namespace allstarr.Core.Providers.AppleDownload;

public sealed class AppleDownloadMetadataCapabilityAdapter(IConcreteMetadataService legacy)
    : ConcreteMetadataCapabilityAdapter(AppleDownloadCapabilityAdapter.StableProviderId, legacy);
