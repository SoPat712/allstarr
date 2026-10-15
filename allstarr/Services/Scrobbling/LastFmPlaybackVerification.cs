using System.Globalization;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Services.Common;

namespace allstarr.Services.Scrobbling;

public sealed partial class LastFmScopedPlaybackScrobbleTarget
{
    public Task<PlaybackScrobbleVerification> VerifyAsync(IntelligenceScope scope, ProviderAccountContext account,
        PlaybackScrobbleSubmission submission, CancellationToken token) => accounts.UseExactAsync(scope, account,
            (secret, ct) => VerifyHistoryAsync(secret, submission, ct), token);

    private async Task<PlaybackScrobbleVerification> VerifyHistoryAsync(JsonElement secret,
        PlaybackScrobbleSubmission submission, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var username = Required(secret, "username");
        var timestamp = submission.OccurredAt.ToUnixTimeSeconds();
        var url = "https://ws.audioscrobbler.com/2.0/?method=user.getRecentTracks&format=json&limit=200&page=1" +
            $"&user={Uri.EscapeDataString(username)}&api_key={Uri.EscapeDataString(Required(secret, "apiKey"))}" +
            $"&from={(timestamp - 2).ToString(CultureInfo.InvariantCulture)}&to={(timestamp + 2).ToString(CultureInfo.InvariantCulture)}";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) return PlaybackScrobbleVerification.Unavailable;
        await response.Content.LoadIntoBufferAsync(256 * 1024, deadline.Token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _) || !root.TryGetProperty("recenttracks", out var recent) ||
            !recent.TryGetProperty("@attr", out var attributes) ||
            !string.Equals(Text(attributes, "user"), username, StringComparison.OrdinalIgnoreCase) ||
            !recent.TryGetProperty("track", out var tracks)) return PlaybackScrobbleVerification.Unavailable;
        var items = tracks.ValueKind == JsonValueKind.Array ? tracks.EnumerateArray().ToArray() :
            tracks.ValueKind == JsonValueKind.Object ? [tracks] : Array.Empty<JsonElement>();
        if (tracks.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return PlaybackScrobbleVerification.Unavailable;
        var windowEntries = 0;
        var looselyMatched = false;
        foreach (var item in items)
        {
            if (item.TryGetProperty("@attr", out var flags) && Text(flags, "nowplaying") == "true") continue;
            if (!item.TryGetProperty("date", out var date) || !long.TryParse(Text(date, "uts"), CultureInfo.InvariantCulture, out var at))
                return PlaybackScrobbleVerification.Unavailable;
            if (Math.Abs(at - timestamp) > 1) continue;
            windowEntries++;
            looselyMatched |= Same(Text(item, "name"), submission.Track.Title) ||
                (item.TryGetProperty("artist", out var artist) &&
                 Same(Text(artist, "#text") ?? Text(artist, "name"), submission.Track.Artist));
        }
        // Last.fm can correct both submitted names. A lone listen at the saved second is sufficient evidence.
        if (looselyMatched || windowEntries == 1) return PlaybackScrobbleVerification.Found;
        if (windowEntries > 1) return PlaybackScrobbleVerification.Unavailable;
        // A truncated or malformed page cannot prove absence. Keep the bounded lookup on the review path.
        return IntProperty(attributes, "page") == 1 && Text(attributes, "totalPages") is "0" or "1"
            ? PlaybackScrobbleVerification.Absent : PlaybackScrobbleVerification.Unavailable;
    }

    private static bool Same(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        FuzzyMatcher.CalculateSimilarityAggressive(FuzzyMatcher.StripDecorators(left), FuzzyMatcher.StripDecorators(right)) >= 85;
}
