namespace allstarr.Core.Health;

public static class ProviderHealthRegistration
{
    public static IServiceCollection AddDurableProviderHealth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(ProviderHealthOptions.SectionName)
                          .Get<ProviderHealthOptions>()
                      ?? new ProviderHealthOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<DurableProviderHealthStore>();
        services.AddSingleton<IDurableProviderHealthObservationStore>(provider =>
            provider.GetRequiredService<DurableProviderHealthStore>());
        services.AddHostedService<DurableProviderHealthInitializer>();
        return services;
    }
}
