namespace allstarr.Core.Identity;

public static class IdentityRegistration
{
    public static IServiceCollection AddPlatformIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var identity = configuration.GetSection(IdentityOptions.SectionName)
                           .Get<IdentityOptions>()
                       ?? new IdentityOptions();
        _ = identity.ParseMode();
        var providerAccounts = configuration.GetSection(ProviderAccountOptions.SectionName)
                                   .Get<ProviderAccountOptions>()
                               ?? new ProviderAccountOptions();
        services.AddSingleton(identity);
        services.AddSingleton(providerAccounts);
        services.AddSingleton<BackendIdentityResolver>();
        services.AddSingleton<ProviderAccountResolver>();
        services.AddHostedService<IdentityBootstrapper>();
        return services;
    }
}
