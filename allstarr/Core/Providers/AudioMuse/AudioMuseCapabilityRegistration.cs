using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.AudioMuse;

public static class AudioMuseCapabilityRegistration
{
    public static IServiceCollection AddAudioMuseIntelligenceCapability(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.AddHttpClient(AudioMuseIntelligenceCapabilityAdapter.HttpClientName, client =>
                client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        services.AddSingleton<AudioMuseIntelligenceCapabilityAdapter>();
        services.AddSingleton<AudioMuseHealthProbeCapabilityAdapter>();
        services.AddSingleton<ProviderRegistration>(provider => CreateRegistration(
            provider.GetRequiredService<AudioMuseIntelligenceCapabilityAdapter>(),
            provider.GetRequiredService<AudioMuseHealthProbeCapabilityAdapter>()));
        return services;
    }

    internal static ProviderRegistration CreateRegistration(
        AudioMuseIntelligenceCapabilityAdapter intelligence,
        AudioMuseHealthProbeCapabilityAdapter health) => new(
        new ProviderDescriptor(
            AudioMuseIntelligenceCapabilityAdapter.StableProviderId,
            "AudioMuse",
            "Connect a self-hosted AudioMuse server for sound maps, similarity search, blends, and discovery.",
            ProviderOrigin.BuiltIn,
            "1",
            "1.0",
            [
                new(ProviderCapabilityKind.Intelligence, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required, "1.0",
                    ["startAnalysis", "getAnalysisProgress", "getClusters", "recommend", "search", "findPath", "blend", "getMap", "disconnect"],
                    [Core.Storage.ProviderAccountScope.Global, Core.Storage.ProviderAccountScope.User,
                        Core.Storage.ProviderAccountScope.Library]),
                new(ProviderCapabilityKind.Health, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required, "1.0", ["probeIntelligence"],
                    [Core.Storage.ProviderAccountScope.Global, Core.Storage.ProviderAccountScope.User,
                        Core.Storage.ProviderAccountScope.Library])
            ],
            new ProviderPermissionDescriptor(secretSettingKeys: ["apiToken"]),
            [
                new("baseUrl", ProviderSettingValueKind.Text, ProviderSettingScope.ProviderAccount,
                    "AudioMuse server URL", true,
                    helpText: "The HTTP or HTTPS address of your self-hosted AudioMuse server."),
                new("apiToken", ProviderSettingValueKind.Secret, ProviderSettingScope.ProviderAccount,
                    "AudioMuse access token",
                    helpText: "Optional. Use the API_TOKEN configured on the AudioMuse server."),
                new("server", ProviderSettingValueKind.Text, ProviderSettingScope.ProviderAccount,
                    "AudioMuse music server",
                    helpText: "Optional server ID or name when AudioMuse indexes more than one music server.")
            ],
            healthProbe: true),
        [intelligence, health]);
}
