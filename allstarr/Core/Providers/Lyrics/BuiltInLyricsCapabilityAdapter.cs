using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Models.Lyrics;
using allstarr.Services.Lyrics;

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
        services.AddSingleton<ProviderRegistration>(provider => CreateRegistration(
            "lyricsplus",
            "LyricsPlus",
            CreateLyricsPlus(provider.GetRequiredService<LyricsPlusService>())));
        services.AddSingleton<ProviderRegistration>(provider => CreateRegistration(
            "lrclib",
            "LRCLib",
            CreateLrclib(provider.GetRequiredService<LrclibService>())));
        return services;
    }

    public static BuiltInLyricsCapabilityAdapter CreateSpotify(SpotifyLyricsService service) => new(
        "spotify",
        async (request, cancellationToken) =>
        {
            var id = request.ProviderTrackId.Value
                .Replace("spotify:track:", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (id.Length != 22 || id.Contains(':') || id.Contains("local", StringComparison.OrdinalIgnoreCase))
                return null;
            // ponytail: legacy service lacks a cancellation parameter; bound the caller now,
            // thread the token into its HttpClient only if orphaned sidecar requests are observed.
            var result = await service.GetLyricsByTrackIdAsync(id).WaitAsync(cancellationToken);
            var lyrics = result == null ? null : service.ToLyricsInfo(result);
            if (lyrics != null) lyrics.Source ??= "spotify";
            return lyrics;
        });

    public static BuiltInLyricsCapabilityAdapter CreateLyricsPlus(LyricsPlusService service) => new(
        "lyricsplus",
        (request, cancellationToken) => service.GetLyricsAsync(
            request.TrackTitle ?? string.Empty,
            request.ArtistNames.ToArray(),
            request.AlbumTitle,
            request.DurationSeconds ?? 0).WaitAsync(cancellationToken));

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
