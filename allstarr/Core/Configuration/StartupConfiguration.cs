using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Configuration;

public static class StartupConfiguration
{
    public static async Task<DurableStorageState> PrepareAsync(IConfiguration configuration, IHostEnvironment environment,
        CancellationToken token = default)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new RedactingConsoleLoggerProvider(configuration)));
        services.AddDurableStorage(configuration, environment);
        services.AddEncryptedSecretStore(configuration);
        await using var scope = services.BuildServiceProvider();
        await scope.GetRequiredService<DurableStorageInitializer>().StartAsync(token);
        var state = scope.GetRequiredService<DurableStorageState>();
        if (state.GetSnapshot().Readiness == DurableStorageReadiness.Ready)
        {
            await using var db = await scope.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync(token);
            var saved = (await db.RuntimeSettings.AsNoTracking().Where(item => item.OwnerUserId == null).ToListAsync(token))
                .Select(DurableRuntimeSettingsService.ProjectStoredRecord).ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var item in saved.Values)
            {
                configuration[item.Key] = item.NormalizedValue;
                configuration[RuntimeSettingCatalog.Require(item.Key).BootstrapKey] = item.NormalizedValue;
                if (item.Key.StartsWith("Library:", StringComparison.Ordinal))
                    foreach (var backend in new[] { "Jellyfin", "Subsonic" })
                        configuration[backend + ":" + item.Key[8..]] = item.NormalizedValue;
            }
            await allstarr.Services.Admin.AdminOidcSettingsService.LoadForStartupAsync(scope, configuration, token);
            if (saved.ContainsKey("Backend:Type"))
            {
                if (!saved.TryGetValue("Backend:Url", out var endpoint) ||
                    !OutboundRequestGuard.TryCreateConfiguredServiceUri(endpoint.NormalizedValue, out _, out _))
                    throw new InvalidOperationException("The saved media-server connection is incomplete. Restore a matching database backup.");
                configuration[configuration["Backend:Type"] + ":Url"] = endpoint.NormalizedValue;
                configuration["Setup:BackendConfigured"] = "true";
                configuration["Setup:BackendSource"] = "database";
                await LoadServiceCredentialAsync(scope, configuration, token);
                return state;
            }
        }
        var legacy = configuration["Setup:LegacyBackendConfigured"] == "true";
        configuration["Setup:BackendConfigured"] = legacy ? "true" : "false";
        configuration["Setup:BackendSource"] = legacy ? "legacy-environment" : "pending";
        if (!legacy)
        {
            configuration["Backend:Type"] = "Jellyfin";
            configuration["Jellyfin:Url"] = "";
            configuration["Subsonic:Url"] = "";
        }
        return state;
    }

    internal static string ServicePurpose(string backend, string endpoint) =>
        "backend-service:" + backend.ToLowerInvariant() + ":" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.TrimEnd('/'))));

    private static async Task LoadServiceCredentialAsync(IServiceProvider services, IConfiguration configuration, CancellationToken token)
    {
        configuration["Jellyfin:ApiKey"] = "";
        configuration["Jellyfin:UserId"] = "";
        configuration["Setup:ServiceCredentialNeedsReview"] = configuration["Backend:Type"] == "Jellyfin" ? "true" : "false";
        if (!Guid.TryParse(configuration["Backend:ServiceCredentialReference"], out var reference)) return;
        try
        {
            using var lease = await services.GetRequiredService<EncryptedSecretStore>().OpenAsync(reference,
                new SecretAccessContext(null, ServicePurpose(configuration["Backend:Type"]!, configuration["Backend:Url"]!), AllowShared: true), token);
            if (configuration["Backend:Type"]!.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase))
            {
                configuration["Jellyfin:ApiKey"] = lease.ReadUtf8();
                configuration["Jellyfin:UserId"] = configuration["Backend:ServiceUserId"];
                configuration["Setup:ServiceCredentialNeedsReview"] = string.IsNullOrWhiteSpace(configuration["Jellyfin:ApiKey"]) ? "true" : "false";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or KeyNotFoundException)
        {
            configuration["Setup:ServiceCredentialNeedsReview"] = "true";
        }
    }
}
