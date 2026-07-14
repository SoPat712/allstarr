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
        var policy = configuration.GetSection(ProviderPolicyOptions.SectionName)
                         .Get<ProviderPolicyOptions>()
                     ?? new ProviderPolicyOptions();
        var providerAccounts = configuration.GetSection(ProviderAccountManagementOptions.SectionName)
                                   .Get<ProviderAccountManagementOptions>()
                               ?? new ProviderAccountManagementOptions();
        _ = providerAccounts.ParseManagementMode();
        services.AddSingleton(identity);
        services.AddSingleton(policy);
        services.AddSingleton(providerAccounts);
        services.AddSingleton<BackendIdentityResolver>();
        services.AddSingleton<ProviderAccountResolver>();
        services.AddHostedService<IdentityBootstrapper>();
        return services;
    }
}
