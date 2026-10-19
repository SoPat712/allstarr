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
        CancellationToken token = default, IBackendConnectionVerifier? serviceVerifier = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new RedactingConsoleLoggerProvider(configuration)));
        services.AddDurableStorage(configuration, environment);
        services.AddEncryptedSecretStore(configuration);
        services.AddHttpClient(BackendConnectionVerifier.HttpClientName).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<IBackendConnectionVerifier, BackendConnectionVerifier>();
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
            await allstarr.Services.Admin.OdesliApiKeySettingsService.LoadForStartupAsync(scope, configuration, token);
            if (saved.ContainsKey("Backend:Type"))
            {
                if (!saved.TryGetValue("Backend:Url", out var endpoint) ||
                    !OutboundRequestGuard.TryCreateConfiguredServiceUri(endpoint.NormalizedValue, out _, out _))
                    throw new InvalidOperationException("The saved media-server connection is incomplete. Restore a matching database backup.");
                configuration[configuration["Backend:Type"] + ":Url"] = endpoint.NormalizedValue;
                configuration["Setup:BackendConfigured"] = "true";
                configuration["Setup:BackendSource"] = "database";
                await LoadServiceCredentialAsync(scope, configuration, saved.GetValueOrDefault("Backend:ServiceCredentialKind"),
                    serviceVerifier ?? scope.GetRequiredService<IBackendConnectionVerifier>(), token);
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

    private static async Task LoadServiceCredentialAsync(IServiceProvider services, IConfiguration configuration, EffectiveRuntimeSetting? savedKind,
        IBackendConnectionVerifier verifier, CancellationToken token)
    {
        configuration["Jellyfin:ApiKey"] = "";
        configuration["Jellyfin:UserId"] = "";
        configuration["Jellyfin:ServiceCredentialKind"] = null;
        configuration["Setup:ServiceCredentialNeedsReview"] = configuration["Backend:Type"] == "Jellyfin" ? "true" : "false";
        if (!Guid.TryParse(configuration["Backend:ServiceCredentialReference"], out var reference)) return;
        try
        {
            using var lease = await services.GetRequiredService<EncryptedSecretStore>().OpenAsync(reference,
                new SecretAccessContext(null, ServicePurpose(configuration["Backend:Type"]!, configuration["Backend:Url"]!), AllowShared: true), token);
            if (configuration["Backend:Type"]!.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase))
            {
                var credential = lease.ReadUtf8();
                var kind = savedKind?.NormalizedValue;
                if (kind is not ("user-token" or "api-key"))
                {
                    bool? actsAsUser;
                    try
                    {
                        actsAsUser = await verifier.VerifyUserTokenAsync(configuration["Backend:Url"]!,
                            configuration["Backend:ServiceUserId"] ?? "", credential, token);
                    }
                    catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OperationCanceledException)
                    {
                        // An unreachable server proves nothing, so use server-key limits now and check again next start.
                        actsAsUser = null;
                    }
                    kind = actsAsUser == true ? "user-token" : "api-key";
                    if (actsAsUser.HasValue)
                    {
                        var settings = new DurableRuntimeSettingsService(services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(),
                            configuration, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
                        await settings.ApplyBatchAsync([new("Backend:ServiceCredentialKind", kind, savedKind?.Revision)], "backend-service-verification", cancellationToken: token);
                        configuration["Backend:ServiceCredentialKind"] = kind;
                    }
                }
                configuration["Jellyfin:ServiceCredentialKind"] = kind;
                configuration["Jellyfin:ApiKey"] = credential;
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
