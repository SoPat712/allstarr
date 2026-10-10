using allstarr.Core.Storage;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleDownload;
using allstarr.Core.Providers.Spotify;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.AppleMusicKit;

public static class AppleMusicKitPlaylistCapabilityRegistration
{
    public static IServiceCollection AddAppleMusicProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.TryAddSingleton<IProviderAccountSettingsReader, ProviderAccountSettingsReader>();
        foreach (var name in new[] { AppleWebTokenProvider.HttpClientName, AppleMusicClient.HttpClientName })
            services.AddHttpClient(name, client => client.Timeout = TimeSpan.FromSeconds(30))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton(provider => new AppleWebTokenProvider(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(AppleWebTokenProvider.HttpClientName)));
        services.AddSingleton(provider => new AppleMusicClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(AppleMusicClient.HttpClientName),
            provider.GetRequiredService<AppleWebTokenProvider>(), provider.GetRequiredService<IProviderAccountSecretAccessor>(),
            provider.GetRequiredService<IProviderAccountSettingsReader>()));
        services.AddSingleton<AppleMusicKitMetadataCapabilityAdapter>();
        services.AddSingleton(provider => new AppleMusicKitPlaylistCapabilityAdapter(
            provider.GetRequiredService<AppleMusicClient>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(AppleMusicClient.HttpClientName)));
        services.AddAppleDownloadCapability();
        services.AddSingleton<ProviderRegistration>(provider => CreateRegistration(
            provider.GetRequiredService<AppleMusicKitMetadataCapabilityAdapter>(),
            provider.GetRequiredService<AppleMusicKitPlaylistCapabilityAdapter>(),
            provider.GetRequiredService<AppleDownloadStreamingCapabilityAdapter>(),
            provider.GetRequiredService<AppleDownloadCapabilityAdapter>(),
            provider.GetRequiredService<AppleDownloadLyricsCapabilityAdapter>()));
        return services;
    }
    public static ProviderRegistration CreateRegistration(AppleMusicKitMetadataCapabilityAdapter metadata,
        AppleMusicKitPlaylistCapabilityAdapter playlist, IProviderStreamingCapability streaming,
        IProviderDownloadCapability download, IProviderLyricsCapability lyrics) => new(
            AppleMusicKitPlaylistCapabilityAdapter.Descriptor([
                AppleMusicKitPlaylistCapabilityAdapter.MetadataDescriptor,
                AppleMusicKitPlaylistCapabilityAdapter.PlaylistDescriptor,
                new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                    "1", ["getStreamLease", "probeStream"], [ProviderAccountScope.Personal]),
                new(ProviderCapabilityKind.Download, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                    "1", ["download", "checkAvailability"], [ProviderAccountScope.Personal]),
                new(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                    "1", ["fetchLyrics"], [ProviderAccountScope.Personal])]),
            [metadata, playlist, streaming, download, lyrics], ["apple-download", "applemusic"]);

}
