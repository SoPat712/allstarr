using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Music;

public sealed record SimilarTrackSuggestion(string Artist, string Title);

internal sealed record LastFmSimilarTracksResult(IReadOnlyList<SimilarTrackSuggestion> Suggestions, string Outcome);

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
        if (!container.TryGetProperty("track", out var tracks)) return [];
        return tracks.ValueKind switch
        {
            JsonValueKind.Array => tracks.EnumerateArray().ToArray(),
            JsonValueKind.Object => [tracks],
            JsonValueKind.Null => [],
            _ => throw new JsonException("Last.fm track list is malformed.")
        };
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
                if (!response.IsSuccessStatusCode) return Empty($"http-{(int)response.StatusCode}");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (document.RootElement.TryGetProperty("error", out var error))
                    return Empty(error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out var code)
                        ? $"api-error-{code}"
                        : "api-error");
                if (!document.RootElement.TryGetProperty("similartracks", out var similar)) return Empty("empty");
                var suggestions = TrackElements(similar)
                    .Select(track => new SimilarTrackSuggestion(
                        track.TryGetProperty("artist", out var credit) && credit.TryGetProperty("name", out var name)
                            ? name.GetString() ?? ""
                            : "",
                        track.TryGetProperty("name", out var trackName) ? trackName.GetString() ?? "" : ""))
                    .Where(suggestion => suggestion.Artist.Length > 0 && suggestion.Title.Length > 0)
                    .Take(limit)
                    .ToList();
                return new LastFmSimilarTracksResult(suggestions, suggestions.Count > 0 ? "lastfm: ok" : "lastfm: empty");
            }, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Empty("timeout");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug("Last.fm similar tracks were unavailable ({ExceptionType})", error.GetType().Name);
            return Empty("unavailable");
        }
    }

    private static LastFmSimilarTracksResult Empty(string outcome) => new([], $"lastfm: {outcome}");
}
