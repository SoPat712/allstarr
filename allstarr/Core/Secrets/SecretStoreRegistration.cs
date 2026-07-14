using allstarr.Core.Operations;

namespace allstarr.Core.Secrets;

public static class SecretStoreRegistration
{
    public static IServiceCollection AddEncryptedSecretStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(SecretStoreOptions.SectionName)
                          .Get<SecretStoreOptions>()
                      ?? new SecretStoreOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IPlatformClock, SystemPlatformClock>();
        services.AddSingleton<FileSecretKeyRingProvider>();
        services.AddSingleton<EncryptedSecretStore>();
        return services;
    }
}
