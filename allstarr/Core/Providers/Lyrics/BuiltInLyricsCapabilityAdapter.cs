using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Models.Lyrics;
using allstarr.Services.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class BuiltInLyricsCapabilityAdapter(
    string providerId,
    Func<ProviderLyricsRequest, CancellationToken, Task<LyricsInfo?>> fetch)
    : IProviderLyricsCapability
{
    public string ProviderId { get; } = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    public async Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
        ProviderExecutionContext context,
        ProviderLyricsRequest request)
    {
        try
        {
            context.RequireResourceOwner(request.ProviderTrackId, ProviderResourceKind.Track);
            if (context.ProviderId != ProviderId || !context.Policy.AllowsProvider(ProviderId))
                return Failure(ProviderErrorKind.Forbidden);
            context.CancellationToken.ThrowIfCancellationRequested();

            var lyrics = await fetch(request, context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
            if (lyrics == null)
                return Available(ProviderLyricsAvailabilityState.Unavailable);

            var (format, content) = SelectContent(lyrics, request.PreferredFormat);
            if (string.IsNullOrWhiteSpace(content))
                return Available(ProviderLyricsAvailabilityState.Unavailable);
            var revision = lyrics.Revision ?? "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            return Available(
                ProviderLyricsAvailabilityState.Available,
                request.AvailabilityOnly ? null : format,
                request.AvailabilityOnly ? null : content,
                revision,
                lyrics.Source);
        }
        catch (OperationCanceledException)
        {
            return Failure(ProviderErrorKind.Canceled);
        }
        catch
        {
            return Failure(ProviderErrorKind.TransientFailure);
        }
    }

    private ProviderOutcome<ProviderLyricsResult> Available(
        ProviderLyricsAvailabilityState state,
        ProviderLyricsFormat? format = null,
        string? content = null,
        string? revision = null,
        string? source = null) =>
        ProviderOutcome<ProviderLyricsResult>.Success(new(
            state, source ?? ProviderId, format, content, revision));

    private static (ProviderLyricsFormat Format, string? Content) SelectContent(
        LyricsInfo lyrics,
        ProviderLyricsFormat? preferred) =>
        preferred == ProviderLyricsFormat.PlainText && !string.IsNullOrWhiteSpace(lyrics.PlainLyrics)
            ? (ProviderLyricsFormat.PlainText, lyrics.PlainLyrics)
            : !string.IsNullOrWhiteSpace(lyrics.SyncedLyrics)
                ? (ProviderLyricsFormat.LineTimed, lyrics.SyncedLyrics)
                : (ProviderLyricsFormat.PlainText, lyrics.PlainLyrics);

    private static ProviderOutcome<ProviderLyricsResult> Failure(ProviderErrorKind kind) =>
        ProviderOutcome<ProviderLyricsResult>.Failure(new(kind));
}

public static class BuiltInLyricsCapabilityRegistration
{
    public static IServiceCollection AddBuiltInLyricsCapabilities(this IServiceCollection services)
    {
        services.AddHttpClient(LrcRedCapabilityAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton(provider => new LrcRedCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(LrcRedCapabilityAdapter.HttpClientName),
            provider.GetRequiredService<IApplicationCache>(),
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>>().Value.LyricsTTL));
        services.AddSingleton<ProviderRegistration>(provider => PublicRegistration(
            "lrcred", "lrc.red", "Public ISRC and metadata lookup with word-synced TTML.",
            provider.GetRequiredService<LrcRedCapabilityAdapter>()));
        services.AddHttpClient(UnisonCapabilityAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton(provider => new UnisonCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(UnisonCapabilityAdapter.HttpClientName),
            provider.GetRequiredService<IApplicationCache>(),
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>>().Value.LyricsTTL));
        services.AddSingleton<ProviderRegistration>(provider => PublicRegistration(
            "unison", "Unison", "Lyrics from Unison (https://unison.boidu.dev)",
            provider.GetRequiredService<UnisonCapabilityAdapter>()));
        services.AddHttpClient(PaxsenixCapabilityAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton(provider => new PaxsenixCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(PaxsenixCapabilityAdapter.HttpClientName),
            provider.GetRequiredService<IApplicationCache>(),
            provider.GetRequiredService<allstarr.Core.Providers.AppleMusicKit.AppleMusicKitMetadataCapabilityAdapter>(),
            () => provider.GetRequiredService<allstarr.Core.Matching.ITrackIdentityService>(),
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>>().Value.LyricsTTL));
        services.AddSingleton<ProviderRegistration>(provider => PublicRegistration(
            "paxsenix", "Paxsenix", "Apple Music lyrics through Paxsenix; no account required.",
            provider.GetRequiredService<PaxsenixCapabilityAdapter>()));
        services.AddHttpClient(LyricsPlusCapabilityAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton(provider => new LyricsPlusCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(LyricsPlusCapabilityAdapter.HttpClientName),
            provider.GetRequiredService<IApplicationCache>(),
            async token => (string)(await provider.GetRequiredService<allstarr.Core.Settings.IDurableRuntimeSettings>()
                .GetAsync("LyricsPlus:BaseUrl", token)).Value,
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>>().Value.LyricsTTL));
        services.AddSingleton<ProviderRegistration>(provider => new ProviderRegistration(
            new ProviderDescriptor("lyricsplus", "LyricsPlus", "Public lyrics with instance fallback or a self-hosted endpoint.",
                ProviderOrigin.BuiltIn, "1", "lyricsplus-v2",
                [new(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["fetchLyrics"])],
                new ProviderPermissionDescriptor()), [provider.GetRequiredService<LyricsPlusCapabilityAdapter>()]));
        services.AddHttpClient(MusixmatchLyricsCapabilityAdapter.HttpClientName)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton(provider => new MusixmatchLyricsCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(MusixmatchLyricsCapabilityAdapter.HttpClientName),
            provider.GetRequiredService<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            provider.GetRequiredService<IApplicationCache>(),
            provider.GetRequiredService<ILogger<MusixmatchLyricsCapabilityAdapter>>()));
        services.AddSingleton<ProviderRegistration>(provider => MusixmatchLyricsCapabilityAdapter.CreateRegistration(
            provider.GetRequiredService<MusixmatchLyricsCapabilityAdapter>()));
        services.AddSingleton<ProviderRegistration>(provider => CreateRegistration(
            "lrclib",
            "LRCLib",
            CreateLrclib(provider.GetRequiredService<LrclibService>())));
        services.AddHttpClient(BiniLyricsCapabilityAdapter.HttpClientName)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton(provider => new BiniLyricsCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(BiniLyricsCapabilityAdapter.HttpClientName),
            provider.GetRequiredService<IApplicationCache>(),
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>>().Value.LyricsTTL,
            provider.GetRequiredService<ILogger<BiniLyricsCapabilityAdapter>>()));
        services.AddSingleton<ProviderRegistration>(provider => PublicRegistration(
            "binilyrics", "BiniLyrics", "Public identity-checked TTML lyrics; no account required.",
            provider.GetRequiredService<BiniLyricsCapabilityAdapter>()));
        services.AddHttpClient(GeniusCapabilityAdapter.HttpClientName)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton(provider => new GeniusCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(GeniusCapabilityAdapter.HttpClientName),
            provider.GetRequiredService<IApplicationCache>(),
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>>().Value.LyricsTTL,
            provider.GetRequiredService<ILogger<GeniusCapabilityAdapter>>()));
        services.AddSingleton<ProviderRegistration>(provider => PublicRegistration(
            "genius", "Genius", "Public plain lyrics through the Genius worker; no account required.",
            provider.GetRequiredService<GeniusCapabilityAdapter>()));
        return services;
    }

    private static ProviderRegistration PublicRegistration(string id, string name, string description, IProviderLyricsCapability adapter) => new(
        new ProviderDescriptor(id, name, description, ProviderOrigin.BuiltIn, "1", "ttml-lyrics-v1",
            [new(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["fetchLyrics"])],
            new ProviderPermissionDescriptor()), [adapter]);

    public static BuiltInLyricsCapabilityAdapter CreateLrclib(LrclibService service) => new(
        "lrclib",
        async (request, cancellationToken) =>
        {
            var lyrics = await service.GetLyricsAsync(
                request.TrackTitle ?? string.Empty,
                request.ArtistNames.ToArray(),
                request.AlbumTitle ?? string.Empty,
                request.DurationSeconds ?? 0).WaitAsync(cancellationToken);
            if (lyrics != null) lyrics.Source ??= "lrclib";
            return lyrics;
        });

    public static ProviderRegistration CreateRegistration(
        string id,
        string name,
        BuiltInLyricsCapabilityAdapter adapter) => new(
        new ProviderDescriptor(
            id,
            name,
            $"Typed {name} lyrics lookup with plain/timed content and stable revisions.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "lyrics-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Lyrics,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    "1",
                    ["fetchLyrics"])
            ],
            new ProviderPermissionDescriptor()),
        [adapter]);
}
