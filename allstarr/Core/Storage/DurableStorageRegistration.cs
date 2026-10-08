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
        var dataDirectory = Path.GetFullPath(options.DataDirectory);
        foreach (var (key, relativePath) in new Dictionary<string, string>
        {
            ["Library:DownloadPath"] = "downloads",
            ["Library:KeptPath"] = "kept",
            ["Extensions:Directory"] = "extensions",
            ["Cache:MediaDirectory"] = Path.Combine("cache", "media"),
            ["Downloads:Workspace:RootPath"] = Path.Combine("cache", "download-workspaces"),
            ["Intelligence:HistoryImport:RootPath"] = Path.Combine("cache", "listening-history-imports")
        })
        {
            if (string.IsNullOrWhiteSpace(configuration[key]))
                configuration[key] = Path.Combine(dataDirectory, relativePath);
        }
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
