namespace allstarr.Core.Operations;

public static class OperationsRegistration
{
    public static IServiceCollection AddPlatformOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var readiness = configuration.GetSection(ReadinessOptions.SectionName)
                            .Get<ReadinessOptions>()
                        ?? new ReadinessOptions();
        var sidecars = configuration.GetSection(SidecarHealthOptions.SectionName)
                           .Get<SidecarHealthOptions>()
                       ?? new SidecarHealthOptions();
        sidecars.Validate();
        services.AddSingleton(readiness);
        services.AddSingleton(sidecars);
        services.AddSingleton<OperationalRuntimeState>();
        services.AddSingleton<PlatformTraceCollector>();
        services.AddHostedService(provider => provider.GetRequiredService<PlatformTraceCollector>());
        services.AddSingleton<SidecarStatusCatalog>();
        services.AddSingleton<PlatformReadinessService>();
        services.AddHostedService<SidecarHealthMonitor>();
        return services;
    }
}
