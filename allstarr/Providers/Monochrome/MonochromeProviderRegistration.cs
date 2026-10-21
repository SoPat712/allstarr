using allstarr.Core.Capabilities;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.Monochrome;

public static class MonochromeProviderRegistration
{
    public static IServiceCollection AddMonochromeProvider(this IServiceCollection services)
    {
        services.AddHttpClient("Monochrome", client => client.Timeout = TimeSpan.FromSeconds(5))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false
            });
        services.TryAddSingleton<MonochromeStreamingCapabilityAdapter>(provider => new(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("Monochrome"),
            provider.GetRequiredService<ILogger<MonochromeStreamingCapabilityAdapter>>()));
        services.AddSingleton<ProviderRegistration>(provider => CreateRegistration(
            provider.GetRequiredService<MonochromeStreamingCapabilityAdapter>()));
        return services;
    }

    public static ProviderRegistration CreateRegistration(MonochromeStreamingCapabilityAdapter streaming) => new(
        new ProviderDescriptor("monochrome", "Monochrome", "Account-free lossless streaming for verified recordings.",
            ProviderOrigin.BuiltIn, "1", "monochrome-v1",
            [
                new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None, "1", ["getStreamLease", "probeStream"]),
                new(ProviderCapabilityKind.Health, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None, "1", ["probeStreaming"])
            ],
            new ProviderPermissionDescriptor([new Uri("https://tracks.monochrome.st/")], cache: true),
            branding: new("monochrome"), healthProbe: true),
        [streaming, new MonochromeHealthCapabilityAdapter(streaming)]);
}
