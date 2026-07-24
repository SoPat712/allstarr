using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Storage;

public static class DurableStorageRegistration
{
    public static IServiceCollection AddDurableStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool allowOfflineSqlite = false)
    {
        var options = configuration
            .GetSection(DurableStorageOptions.SectionName)
            .Get<DurableStorageOptions>() ?? new DurableStorageOptions();
        var provider = options.ParseProvider();
        if (provider != DurableStorageProvider.Postgres && !allowOfflineSqlite)
        {
            throw new InvalidOperationException(
                "Allstarr application runtime requires PostgreSQL. " +
                "SQLite is supported only by the offline storage migration commands.");
        }
        options.ApplyPasswordFile(provider);
        if (environment.IsEnvironment("Testing"))
        {
            options.EnforceMutationGuard = false;
        }

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(options);
        services.AddSingleton<DurableStorageState>();
        services.AddSingleton<IDurableStorageRuntimeProbe, DurableStorageRuntimeProbe>();
        services.AddSingleton<DurableMigrationLock>();
        services.AddSingleton<IStorageProcessRunner, StorageProcessRunner>();
        services.AddSingleton<IDurableRestoreTargetVerifier, DurableRestoreTargetVerifier>();
        services.AddSingleton<DurableBackupService>();
        services.AddSingleton<DurableStateTransferService>();
        services.AddSingleton<allstarr.Core.Operations.OperationalMetricsService>();
        services.AddDbContextFactory<AllstarrDbContext>(builder =>
        {
            if (provider == DurableStorageProvider.Postgres)
            {
                builder.UseNpgsql(options.ConnectionString, postgres =>
                {
                    postgres.CommandTimeout(options.CommandTimeoutSeconds);
                });
            }
            else
            {
                builder.UseSqlite(options.ConnectionString, sqlite =>
                {
                    sqlite.CommandTimeout(options.CommandTimeoutSeconds);
                });
            }
        });
        services.AddSingleton<DurableStorageInitializer>();
        services.AddHostedService(provider => provider.GetRequiredService<DurableStorageInitializer>());
        services.AddHostedService<DurableStorageRuntimeMonitor>();
        return services;
    }
}
