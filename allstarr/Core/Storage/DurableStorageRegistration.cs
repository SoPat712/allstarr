using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Storage;

public static class DurableStorageRegistration
{
    public static IServiceCollection AddDurableStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration
            .GetSection(DurableStorageOptions.SectionName)
            .Get<DurableStorageOptions>() ?? new DurableStorageOptions();
        var provider = options.ParseProvider();
        options.ApplyPasswordFile(provider);
        if (environment.IsEnvironment("Testing"))
        {
            options.EnforceMutationGuard = false;
        }

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(options);
        services.AddSingleton<DurableStorageState>();
        services.AddSingleton<IStorageProcessRunner, StorageProcessRunner>();
        services.AddSingleton<IDurableRestoreTargetVerifier, DurableRestoreTargetVerifier>();
        services.AddSingleton<DurableBackupService>();
        services.AddDbContextFactory<AllstarrDbContext>(builder =>
        {
            builder.UseNpgsql(options.ConnectionString, postgres =>
            {
                postgres.CommandTimeout(options.CommandTimeoutSeconds);
            });
        });
        services.AddSingleton<DurableStorageInitializer>();
        services.AddHostedService(provider => provider.GetRequiredService<DurableStorageInitializer>());
        return services;
    }
}
