using Microsoft.Extensions.DependencyInjection.Extensions;
using allstarr.Core.Routing;

namespace allstarr.Core.Capabilities;

public static class ProviderCapabilityRegistration
{
    public static IServiceCollection AddProviderCapabilities(this IServiceCollection services)
    {
        services.TryAddSingleton<ProviderRegistry>();
        services.TryAddSingleton<IProviderRegistry>(provider =>
            provider.GetRequiredService<ProviderRegistry>());
        services.TryAddSingleton<IDynamicProviderRegistry>(provider =>
            provider.GetRequiredService<ProviderRegistry>());
        services.TryAddSingleton<IProviderRouteAccountResolver, DurableProviderRouteAccountResolver>();
        services.TryAddSingleton<IProviderRouteHealthSource, DurableProviderRouteHealthSource>();
        services.TryAddSingleton<IProviderRouteSidecarSource, DurableProviderRouteSidecarSource>();
        services.TryAddSingleton<IProviderRouteDecisionStore, DurableProviderRouteDecisionStore>();
        services.TryAddSingleton<IProviderRouter, ProviderRouter>();
        return services;
    }

    public static IServiceCollection AddProviderDescriptor(
        this IServiceCollection services,
        ProviderDescriptor descriptor)
    {
        var registration = new ProviderRegistration(descriptor);
        ProviderRegistrationValidator.Validate(registration);
        services.AddSingleton(registration);
        return services;
    }

    public static IServiceCollection AddProviderRegistration(
        this IServiceCollection services,
        ProviderRegistration registration)
    {
        ProviderRegistrationValidator.Validate(registration);
        services.AddSingleton(registration);
        return services;
    }
}
