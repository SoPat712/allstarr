using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Music;

/// <summary>Optional public suggestions for an already-known recording; never discovers or writes identity.</summary>
public sealed class ListenBrainzSimilarTracks(
    IHttpClientFactory clients,
    ITrackIdentityService identities,
    IApplicationCache cache,
    ILogger<ListenBrainzSimilarTracks> logger,
    IOptions<PlaybackSettings>? playback = null)
{
    private const string Algorithm = "session_based_days_7500_session_300_contribution_5_threshold_15_limit_50_skip_30_top_n_listeners_1000";
    private readonly PlaybackSettings _playback = playback?.Value ?? new();

    public async Task<IReadOnlyList<SimilarTrackSuggestion>> FindAsync(ProtocolExecutionContext context, string publicId,
        int limit, CancellationToken cancellationToken)
    {
        if (!_playback.ListenBrainzSimilarEnabled) return [];
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Actor == null || limit <= 0) return [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var identity = await identities.ResolveRecordingAsync(context.Actor, publicId, deadline.Token);
            if (!Guid.TryParse(identity?.MusicBrainzRecordingId, out var mbid)) return [];
            var key = $"music:listenbrainz:similar:{mbid:D}";
            var cached = await cache.GetAsync<SimilarTrackSuggestion[]>(key);
            if (cached != null) return cached.Take(Math.Min(limit, 50)).ToArray();
            using var client = clients.CreateClient(nameof(ListenBrainzSimilarTracks));
            using var response = await client.GetAsync(
                $"https://labs.api.listenbrainz.org/similar-recordings/json?algorithm={Algorithm}&recording_mbids={mbid:D}",
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) return [];
            await response.Content.LoadIntoBufferAsync(256 * 1024, deadline.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var seen = new HashSet<Guid> { mbid };
            var result = new List<SimilarTrackSuggestion>();
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (!Guid.TryParse(Text(row, "reference_mbid"), out var reference) || reference != mbid ||
                    !Guid.TryParse(Text(row, "recording_mbid"), out var recording) || !seen.Add(recording)) continue;
                var artist = Text(row, "artist_credit_name");
                var title = Text(row, "recording_name");
                if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) continue;
                result.Add(new(artist, title));
                if (result.Count == 50) break;
            }
            await cache.SetAsync(key, result.ToArray(), TimeSpan.FromMinutes(15));
            return result.Take(limit).ToArray();
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("ListenBrainz similar recordings were unavailable ({ExceptionType})", error.GetType().Name);
            return [];
        }
    }

    private static string? Text(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
