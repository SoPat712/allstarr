using System.Globalization;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Models.Settings;
using allstarr.Services.MusicBrainz;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Enrichment;

public sealed class SharedTagSources(
    IMusicBrainzCatalogClient musicBrainz, IHttpClientFactory clients,
    IOptionsMonitor<ScrobblingSettings> settings, IPlatformClock clock, TimeProvider? timeProvider = null)
{
    private const string LastFmClientName = nameof(SharedTagSources);
    private const int MaximumLastFmResponseBytes = 256 * 1024;
    private static readonly TimeSpan LastFmTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LastFmInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _lastFmRateLimit = new(1, 1);
    private DateTimeOffset _lastLastFmRequestAt = DateTimeOffset.MinValue;

    public async Task<SharedRecordingTags> CollectRecordingAsync(
        string? mbid, string? isrc, string artist, string title, CancellationToken cancellationToken)
    {
        var batches = new List<SharedTagBatch>();
        MusicBrainzRecording? recording = null;
        if (MusicBrainzService.NormalizeMbid(mbid) is { } normalizedMbid)
            recording = await musicBrainz.LookupByMbidAsync(normalizedMbid, cancellationToken);
        if (recording == null && MusicBrainzService.NormalizeIsrc(isrc) is { } normalizedIsrc)
            recording = await musicBrainz.LookupByIsrcAsync(normalizedIsrc, cancellationToken);

        string? artistId = null;
        if (recording != null)
        {
            AddMusicBrainzBatch(batches, "musicbrainz-recording", recording.Genres, recording.Tags);
            var groups = recording.Releases?.Select(item => item.ReleaseGroup)
                .OfType<MusicBrainzReleaseGroup>().ToArray() ?? [];
            AddMusicBrainzBatch(batches, "musicbrainz-release-group",
                groups.SelectMany(item => item.Genres ?? new List<MusicBrainzGenre>()).ToArray(),
                groups.SelectMany(item => item.Tags ?? new List<MusicBrainzTag>()).ToArray());
            artistId = recording.ArtistCredit?.Select(item => MusicBrainzService.NormalizeMbid(item.Artist?.Id))
                .FirstOrDefault(item => item != null);
        }
        else if (MusicBrainzService.NormalizeMbid(mbid) != null || MusicBrainzService.NormalizeIsrc(isrc) != null)
        {
            batches.Add(new("musicbrainz-recording", [], clock.UtcNow));
            batches.Add(new("musicbrainz-release-group", [], clock.UtcNow));
        }

        var apiKey = settings.CurrentValue.LastFm.ApiKey?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(artist) &&
            !string.IsNullOrWhiteSpace(title) &&
            await GetLastFmBatchAsync("track.getTopTags", "artist", artist, "track", title,
                "lastfm-track", apiKey, cancellationToken) is { } trackTags)
            batches.Add(trackTags);
        return new(batches, artistId);
    }

    public async Task<IReadOnlyList<SharedTagBatch>> CollectArtistAsync(
        string? mbid, string artist, CancellationToken cancellationToken)
    {
        var batches = new List<SharedTagBatch>();
        if (MusicBrainzService.NormalizeMbid(mbid) is { } normalizedMbid)
        {
            var evidence = await musicBrainz.LookupArtistByMbidAsync(normalizedMbid, cancellationToken);
            AddMusicBrainzBatch(batches, "musicbrainz-artist", evidence?.Genres, evidence?.Tags);
        }

        var apiKey = settings.CurrentValue.LastFm.ApiKey?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(artist) &&
            await GetLastFmBatchAsync("artist.getTopTags", "artist", artist, null, null,
                "lastfm-artist", apiKey, cancellationToken) is { } artistTags)
            batches.Add(artistTags);
        return batches;
    }

    private void AddMusicBrainzBatch(ICollection<SharedTagBatch> batches, string source,
        IReadOnlyList<MusicBrainzGenre>? genres, IReadOnlyList<MusicBrainzTag>? tags)
    {
        var values = new List<SharedTagInput>();
        if (genres != null)
            values.AddRange(genres.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new SharedTagInput(item.Name!, item.Count, true)));
        if (tags != null)
            values.AddRange(tags.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new SharedTagInput(item.Name!, item.Count)));
        batches.Add(new(source, values, clock.UtcNow));
    }

    private async Task<SharedTagBatch?> GetLastFmBatchAsync(string method, string firstKey, string firstValue,
        string? secondKey, string? secondValue, string source, string apiKey, CancellationToken cancellationToken)
    {
        await _lastFmRateLimit.WaitAsync(cancellationToken);
        try
        {
            var delay = LastFmInterval - (_timeProvider.GetUtcNow() - _lastLastFmRequestAt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _timeProvider, cancellationToken);
            _lastLastFmRequestAt = _timeProvider.GetUtcNow();
        }
        finally { _lastFmRateLimit.Release(); }

        using var timeout = new CancellationTokenSource(LastFmTimeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var query = $"method={Uri.EscapeDataString(method)}&format=json&{firstKey}={Uri.EscapeDataString(firstValue)}";
            if (!string.IsNullOrWhiteSpace(secondKey) && secondValue != null)
                query += $"&{secondKey}={Uri.EscapeDataString(secondValue)}";
            query += $"&api_key={Uri.EscapeDataString(apiKey)}";
            using var response = await clients.CreateClient(LastFmClientName).GetAsync(
                $"https://ws.audioscrobbler.com/2.0/?{query}",
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
                throw TemporarilyUnavailable(RetryAfter(response));
            if (!response.IsSuccessStatusCode) return null;
            using var document = await ReadJsonAsync(response, deadline.Token);
            if (LastFmError(document.RootElement) is { } error)
            {
                if (error == 6) return new(source, [], clock.UtcNow);
                if (error is 11 or 16 or 29) throw TemporarilyUnavailable(null);
                return null;
            }
            return ParseLastFmBatch(document.RootElement, source);
        }
        catch (MusicBrainzLookupException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw TemporarilyUnavailable(null); }
        catch (HttpRequestException exception) { throw TemporarilyUnavailable(null, exception); }
    }

    private SharedTagBatch? ParseLastFmBatch(JsonElement root, string source)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("toptags", out var topTags) || topTags.ValueKind != JsonValueKind.Object ||
            !topTags.TryGetProperty("tag", out var tags))
            return new(source, [], clock.UtcNow);
        IEnumerable<SharedTagInput?> values = tags.ValueKind switch
        {
            JsonValueKind.Array => tags.EnumerateArray().Select(ParseLastFmTag),
            JsonValueKind.Object => [ParseLastFmTag(tags)],
            _ => []
        };
        var parsed = values.OfType<SharedTagInput>().Where(item => item.RawLabel.Length > 0).ToArray();
        return new(source, parsed, clock.UtcNow);
    }

    private static int? LastFmError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        int parsed;
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
            ? parsed : null;
    }

    private static SharedTagInput? ParseLastFmTag(JsonElement tag)
    {
        if (tag.ValueKind != JsonValueKind.Object ||
            !tag.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString())) return null;
        double? weight = null;
        if (tag.TryGetProperty("count", out var count))
        {
            if (count.ValueKind == JsonValueKind.Number && count.TryGetDouble(out var number) &&
                double.IsFinite(number)) weight = number;
            else if (count.ValueKind == JsonValueKind.String &&
                     double.TryParse(count.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                         out number) && double.IsFinite(number)) weight = number;
        }
        return new(name.GetString()!, weight);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaximumLastFmResponseBytes)
            throw new MusicBrainzLookupException("catalog_source_response_too_large",
                "The shared tag source returned more data than Allstarr can safely process.", false);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, token)) > 0)
        {
            if (buffer.Length + read > MaximumLastFmResponseBytes)
                throw new MusicBrainzLookupException("catalog_source_response_too_large",
                    "The shared tag source returned more data than Allstarr can safely process.", false);
            await buffer.WriteAsync(bytes.AsMemory(0, read), token);
        }
        try { return JsonDocument.Parse(buffer.ToArray()); }
        catch (JsonException exception)
        {
            throw new MusicBrainzLookupException("catalog_source_response_invalid",
                "The shared tag source returned invalid metadata.", false, innerException: exception);
        }
    }

    private MusicBrainzLookupException TemporarilyUnavailable(TimeSpan? retryAfter, Exception? inner = null) =>
        new("catalog_source_temporarily_unavailable",
            "The shared tag source is temporarily unavailable.", true, retryAfter, inner);

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - _timeProvider.GetUtcNow();
            return delay <= TimeSpan.Zero ? TimeSpan.Zero :
                delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay;
        }
        return null;
    }
}
