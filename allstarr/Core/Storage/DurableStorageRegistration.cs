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
        var options = configuration.GetSection(StorageOptions.SectionName)
            .Get<StorageOptions>() ?? new StorageOptions();
        options.Validate();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(options);
        services.AddSingleton<DurableStorageState>();
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddSingleton<DurableBackupService>();
        services.AddDbContextFactory<AllstarrDbContext>((provider, builder) =>
            builder.UseSqlite(options.ConnectionString, sqlite =>
                    sqlite.CommandTimeout(options.CommandTimeoutSeconds))
                .AddInterceptors(provider.GetRequiredService<SqlitePragmaInterceptor>()));
        services.AddSingleton<DurableStorageInitializer>();
        services.AddHostedService(provider => provider.GetRequiredService<DurableStorageInitializer>());
        return services;
    }
}
