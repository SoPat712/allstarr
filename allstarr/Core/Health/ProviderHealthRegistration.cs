namespace allstarr.Core.Health;

public static class ProviderHealthRegistration
{
    public static IServiceCollection AddProviderRuntimeHealth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(ProviderHealthOptions.SectionName)
                          .Get<ProviderHealthOptions>()
                      ?? new ProviderHealthOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<ProviderRuntimeHealth>();
        services.AddSingleton<IProviderOutcomeObserver>(provider => provider.GetRequiredService<ProviderRuntimeHealth>());
        return services;
    }
}
