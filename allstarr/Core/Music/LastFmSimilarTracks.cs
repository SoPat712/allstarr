using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Music;

public sealed record SimilarTrackSuggestion(string Artist, string Title);

internal sealed record LastFmSimilarTracksResult(IReadOnlyList<SimilarTrackSuggestion> Suggestions, string Outcome,
    ProviderError? Error = null);

/// <summary>
/// Last.fm track.getSimilar for a listener with a connected Last.fm account. Suggestions are
/// names only; callers resolve them to playable songs. Any failure yields no suggestions.
/// </summary>
public sealed class LastFmSimilarTracks(
    IHttpClientFactory httpClients,
    IScopedRecommendationAccountAccessor accounts,
    ILogger<LastFmSimilarTracks> logger,
    IOptions<PlaybackSettings>? playback = null)
{
    private const string ProviderId = "lastfm";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly PlaybackSettings _playback = playback?.Value ?? new();

    internal static IEnumerable<JsonElement> TrackElements(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object || !container.TryGetProperty("track", out var tracks))
            throw new JsonException("Last.fm track list is missing.");
        return tracks.ValueKind switch
        {
            JsonValueKind.Array => tracks.EnumerateArray().ToArray(),
            JsonValueKind.Object => [tracks],
            _ => throw new JsonException("Last.fm track list is malformed.")
        };
    }

    internal static SimilarTrackSuggestion Suggestion(JsonElement track)
    {
        if (track.ValueKind != JsonValueKind.Object || !track.TryGetProperty("name", out var title) ||
            title.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(title.GetString()) ||
            !track.TryGetProperty("artist", out var artist) || artist.ValueKind != JsonValueKind.Object ||
            !artist.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
            throw new JsonException("Last.fm track metadata is missing.");
        return new(name.GetString()!, title.GetString()!);
    }

    public async Task<IReadOnlyList<SimilarTrackSuggestion>> FindAsync(
        ProtocolExecutionContext context,
        string artist,
        string title,
        int limit,
        CancellationToken cancellationToken) =>
        (await FindWithOutcomeAsync(context, artist, title, limit, cancellationToken)).Suggestions;

    internal async Task<LastFmSimilarTracksResult> FindWithOutcomeAsync(
        ProtocolExecutionContext context,
        string artist,
        string title,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!_playback.LastFmSimilarEnabled) return Empty("disabled");
        artist = MusicLyrics.StripPresentationLabels(artist);
        title = MusicLyrics.StripPresentationLabels(title);
        if (context.Actor?.EffectiveUserId is not { } owner) return Empty("no-listener");
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return Empty("invalid-seed");
        var scope = new IntelligenceScope(owner, context.Protocol.ToString().ToLowerInvariant(), context.BackendInstanceId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            if (!await accounts.HasAccountAsync(scope, ProviderId, deadline.Token)) return Empty("no-account");
            return await accounts.UseAsync(scope, ProviderId, async (secret, token) =>
            {
                if (secret.ValueKind != JsonValueKind.Object || !secret.TryGetProperty("apiKey", out var apiKey) ||
                    apiKey.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(apiKey.GetString()))
                    return Empty("no-key");
                var url = "https://ws.audioscrobbler.com/2.0/?method=track.getsimilar&autocorrect=1&format=json" +
                          $"&limit={Math.Clamp(limit, 1, 50)}" +
                          $"&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}" +
                          $"&api_key={Uri.EscapeDataString(apiKey.GetString()!)}";
                using var response = await httpClients.CreateClient(nameof(LastFmSimilarTracks)).GetAsync(url, token);
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                if (!response.IsSuccessStatusCode) return Empty($"http-{(int)response.StatusCode}", response.StatusCode switch
                {
                    System.Net.HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, retryAfter),
                    System.Net.HttpStatusCode.Forbidden => ProviderError.LastFmPrivateProfile(),
                    System.Net.HttpStatusCode.Unauthorized => new(ProviderErrorKind.AccountNeedsReauthentication),
                    >= System.Net.HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
                    _ => new(ProviderErrorKind.PermanentFailure)
                });
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Last.fm response is malformed.");
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    if (!int.TryParse(error.ToString(), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var code))
                        return Empty("api-error", ProviderError.CompatibilityContractChanged());
                    return Empty($"api-error-{code}", code switch
                    {
                        17 => ProviderError.LastFmPrivateProfile(),
                        10 or 26 => ProviderError.LastFmApiKeyRequired(),
                        4 or 9 => new(ProviderErrorKind.AccountNeedsReauthentication),
                        29 => new(ProviderErrorKind.RateLimited, retryAfter),
                        8 or 11 or 16 => new(ProviderErrorKind.TransientFailure),
                        _ => new(ProviderErrorKind.PermanentFailure)
                    });
                }
                if (!document.RootElement.TryGetProperty("similartracks", out var similar))
                    throw new JsonException("Last.fm track list is missing.");
                var suggestions = TrackElements(similar).Select(Suggestion).ToArray().Take(limit).ToList();
                return new LastFmSimilarTracksResult(suggestions, suggestions.Count > 0 ? "lastfm: ok" : "lastfm: empty");
            }, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Empty("timeout", new(ProviderErrorKind.TransientFailure));
        }
        catch (JsonException)
        {
            return Empty("unavailable", ProviderError.CompatibilityContractChanged());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug("Last.fm similar tracks were unavailable ({ExceptionType})", error.GetType().Name);
            return Empty("unavailable", new(ProviderErrorKind.TransientFailure));
        }
    }

    private static LastFmSimilarTracksResult Empty(string outcome, ProviderError? error = null) => new([], $"lastfm: {outcome}", error);
}
