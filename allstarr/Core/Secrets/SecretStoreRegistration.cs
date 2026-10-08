using allstarr.Core.Operations;
using allstarr.Core.Storage;

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
        if (string.IsNullOrWhiteSpace(configuration[$"{SecretStoreOptions.SectionName}:KeyRingPath"]))
        {
            var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new();
            options.KeyRingPath = Path.Combine(Path.GetFullPath(storage.DataDirectory), "keyring.json");
        }
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IPlatformClock, SystemPlatformClock>();
        services.AddSingleton<FileSecretKeyRingProvider>();
        services.AddSingleton<EncryptedSecretStore>();
        services.AddHostedService<SecretStoreInitializer>();
        return services;
    }
}
