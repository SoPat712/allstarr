using allstarr.Core.Capabilities;
using allstarr.Services.Deezer;

namespace allstarr.Core.Providers.Deezer;

public static class DeezerCapabilityRegistration
{
    public static IServiceCollection AddDeezerMetadataCapability(this IServiceCollection services)
    {
        services.AddSingleton<DeezerMetadataCapabilityAdapter>(provider =>
            new DeezerMetadataCapabilityAdapter(
                provider.GetRequiredService<DeezerMetadataService>()));
        services.AddSingleton<ProviderRegistration>(provider =>
            DeezerMetadataCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<DeezerMetadataCapabilityAdapter>()));
        return services;
    }
}
