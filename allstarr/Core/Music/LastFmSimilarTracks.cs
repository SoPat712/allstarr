using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;

namespace allstarr.Core.Music;

public sealed record SimilarTrackSuggestion(string Artist, string Title);

/// <summary>
/// Last.fm track.getSimilar for a listener with a connected Last.fm account. Suggestions are
/// names only; callers resolve them to playable songs. Any failure yields no suggestions.
/// </summary>
public sealed class LastFmSimilarTracks(
    IHttpClientFactory httpClients,
    IScopedRecommendationAccountAccessor accounts,
    ILogger<LastFmSimilarTracks> logger)
{
    private const string ProviderId = "lastfm";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<SimilarTrackSuggestion>> FindAsync(
        ProtocolExecutionContext context,
        string artist,
        string title,
        int limit,
        CancellationToken cancellationToken)
    {
        if (context.Actor?.EffectiveUserId is not { } owner ||
            string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return [];
        var scope = new IntelligenceScope(owner, context.Protocol.ToString().ToLowerInvariant(), context.BackendInstanceId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            if (!await accounts.HasAccountAsync(scope, ProviderId, deadline.Token)) return [];
            return await accounts.UseAsync(scope, ProviderId, async (secret, token) =>
            {
                if (!secret.TryGetProperty("apiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey.GetString()))
                    return (IReadOnlyList<SimilarTrackSuggestion>)[];
                var url = "https://ws.audioscrobbler.com/2.0/?method=track.getsimilar&autocorrect=1&format=json" +
                          $"&limit={Math.Clamp(limit, 1, 50)}" +
                          $"&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}" +
                          $"&api_key={Uri.EscapeDataString(apiKey.GetString()!)}";
                using var response = await httpClients.CreateClient(nameof(LastFmSimilarTracks)).GetAsync(url, token);
                if (!response.IsSuccessStatusCode) return [];
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!document.RootElement.TryGetProperty("similartracks", out var similar) ||
                    !similar.TryGetProperty("track", out var tracks) || tracks.ValueKind != JsonValueKind.Array) return [];
                return tracks.EnumerateArray()
                    .Select(track => new SimilarTrackSuggestion(
                        track.TryGetProperty("artist", out var credit) && credit.TryGetProperty("name", out var name)
                            ? name.GetString() ?? ""
                            : "",
                        track.TryGetProperty("name", out var trackName) ? trackName.GetString() ?? "" : ""))
                    .Where(suggestion => suggestion.Artist.Length > 0 && suggestion.Title.Length > 0)
                    .Take(limit)
                    .ToList();
            }, deadline.Token);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Last.fm similar tracks were unavailable ({ExceptionType})", error.GetType().Name);
            return [];
        }
    }
}
