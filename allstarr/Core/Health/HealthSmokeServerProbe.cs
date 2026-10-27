using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Health;

public sealed class HealthSmokeServerProbe(HttpClient client, IOptions<JellyfinSettings> settings, TimeProvider time)
{
    public static readonly string[] StepNames = ["backend", "search", "metadata", "playback", "seek", "artwork", "lyrics"];

    private const int MaxJsonBytes = 2 * 1024 * 1024;
    private const int MaxArtworkBytes = 2 * 1024 * 1024;
    private const int AudioProbeBytes = 64 * 1024;
    private const int AudioSeekBytes = 8 * 1024;
    private static readonly TimeSpan AudioSampleCompletionBudget = TimeSpan.FromSeconds(5);
    private readonly HttpClient _client = client;
    private readonly JellyfinSettings _settings = settings.Value;
    private readonly TimeProvider _time = time;

    public async Task<IReadOnlyList<HealthSmokeStep>> RunAsync(
        string query,
        int firstByteBudgetSeconds,
        CancellationToken cancellationToken = default)
    {
        var results = new List<HealthSmokeStep>(StepNames.Length);
        if (!string.Equals(_settings.ServiceCredentialKind, "api-key", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(_settings.ApiKey) ||
            !Uri.TryCreate(_settings.Url, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            return Unavailable();
        }

        var budget = TimeSpan.FromSeconds(Math.Clamp(firstByteBudgetSeconds, 1, 60));
        var backend = await StepAsync("backend", budget, cancellationToken, async token =>
        {
            using var document = await GetJsonAsync("System/Info", null, token);
            if (!document.RootElement.TryGetProperty("Id", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new ProbeFailure("invalid_response");
            return true;
        });
        results.Add(backend.Step);
        if (backend.Step.Status == "failed")
            return AddSkipped(results, "backend_unavailable");

        var search = await StepAsync("search", budget, cancellationToken, async token =>
        {
            var requested = await FindNativeAudioAsync(query, token);
            if (requested != null) return requested;
            var fallback = await FindNativeAudioAsync(null, token);
            return fallback == null ? throw new ProbeSkip("no_native_audio") : fallback with { Fallback = true };
        });
        results.Add(search.Step with
        {
            Status = search.Value?.Fallback == true && search.Step.Status == "passed" ? "skipped" : search.Step.Status,
            Reason = search.Value?.Fallback == true && search.Step.Status == "passed" ? "query_no_match_fallback" : search.Step.Reason
        });
        var selected = search.Value;
        if (selected == null || search.Step.Status == "failed")
            return AddSkipped(results, search.Step.Reason ?? "search_unavailable");

        var metadata = await StepAsync("metadata", budget, cancellationToken, async token =>
        {
            using var document = await GetJsonAsync("Items", new()
            {
                ["Ids"] = selected.Id,
                ["IncludeItemTypes"] = "Audio",
                ["Limit"] = "1",
                ["Fields"] = "Path,Type,MediaSources,ImageTags,ProviderIds"
            }, token);
            if (!document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new ProbeFailure("invalid_response");
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Id", out var id) &&
                    id.ValueKind == JsonValueKind.String && string.Equals(id.GetString(), selected.Id, StringComparison.Ordinal) &&
                    item.TryGetProperty("Type", out var type) &&
                    string.Equals(type.GetString(), "Audio", StringComparison.OrdinalIgnoreCase))
                    return new Metadata(item.Clone());
            }
            throw new ProbeFailure("invalid_response");
        });
        results.Add(metadata.Step);
        var metadataValue = metadata.Value;
        if (metadataValue == null || metadata.Step.Status == "failed")
        {
            results.Add(new("playback", "skipped", 0, "metadata_unavailable"));
            results.Add(new("seek", "skipped", 0, "metadata_unavailable"));
            results.Add(new("artwork", "skipped", 0, "metadata_unavailable"));
            results.Add(new("lyrics", "skipped", 0, "metadata_unavailable"));
            return results;
        }

        var playback = await StepAsync("playback", budget, cancellationToken,
            token => ReadAudioAsync(selected.Id, 0, AudioProbeBytes, token, cancellationToken),
            audio => audio.FirstByteLatencyMilliseconds);
        results.Add(playback.Step);
        if (playback.Value is { TotalLength: > AudioSeekBytes } audio)
        {
            var start = Math.Max(AudioSeekBytes, audio.TotalLength!.Value - AudioSeekBytes);
            var seek = await StepAsync("seek", budget, cancellationToken,
                token => ReadAudioAsync(selected.Id, start, AudioSeekBytes, token, cancellationToken,
                    requirePartial: true, expectedTotal: audio.TotalLength),
                audio => audio.FirstByteLatencyMilliseconds);
            results.Add(seek.Step);
        }
        else
        {
            results.Add(new("seek", "skipped", 0, "range_length_unavailable"));
        }

        var artwork = await StepAsync("artwork", budget, cancellationToken, async token =>
        {
            if (!TryGetImageTag(metadataValue.Root, out var tag)) throw new ProbeSkip("artwork_unavailable");
            using var response = await SendAsync($"Items/{Uri.EscapeDataString(selected.Id)}/Images/Primary",
                new() { ["tag"] = tag, ["maxWidth"] = "300", ["maxHeight"] = "300" }, null, token);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new ProbeSkip("artwork_unavailable");
            EnsureSuccess(response);
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                throw new ProbeFailure("invalid_response");
            var bytes = await ReadBoundedAsync(response.Content, MaxArtworkBytes, token);
            if (bytes.Length == 0) throw new ProbeFailure("empty_body");
            return true;
        });
        results.Add(artwork.Step);

        var lyrics = await StepAsync("lyrics", budget, cancellationToken, async token =>
        {
            using var response = await SendAsync($"Audio/{Uri.EscapeDataString(selected.Id)}/Lyrics", null, null, token);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new ProbeSkip("lyrics_unavailable");
            EnsureSuccess(response);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, MaxJsonBytes, token));
            if (!ContainsLyrics(document.RootElement)) throw new ProbeSkip("lyrics_unavailable");
            return true;
        });
        results.Add(lyrics.Step);
        return results;
    }

    private async Task<SearchResult?> FindNativeAudioAsync(string? query, CancellationToken token)
    {
        var parameters = new Dictionary<string, string>
        {
            ["IncludeItemTypes"] = "Audio",
            ["Recursive"] = "true",
            ["Limit"] = "50",
            ["Fields"] = "Path,Type,ImageTags,ProviderIds"
        };
        if (!string.IsNullOrWhiteSpace(query)) parameters["SearchTerm"] = query;
        using var document = await GetJsonAsync("Items", parameters, token);
        if (!document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new ProbeFailure("invalid_response");
        foreach (var item in items.EnumerateArray())
        {
            if (!IsNativeAudio(item) || !item.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString())) continue;
            return new SearchResult(id.GetString()!, false);
        }
        return null;
    }

    private async Task<AudioRead> ReadAudioAsync(string itemId, long start, int maximumBytes,
        CancellationToken firstByteToken, CancellationToken callerToken, bool requirePartial = false,
        long? expectedTotal = null)
    {
        var started = _time.GetTimestamp();
        using var response = await SendAsync($"Items/{Uri.EscapeDataString(itemId)}/File",
            new() { ["static"] = "true" }, new RangeHeaderValue(start, start + maximumBytes - 1), firstByteToken,
            HttpCompletionOption.ResponseHeadersRead);
        EnsureSuccess(response);
        if (IsTextualContent(response.Content.Headers.ContentType?.MediaType))
            throw new ProbeFailure("invalid_audio");
        if (requirePartial && response.StatusCode != HttpStatusCode.PartialContent)
            throw new ProbeFailure("range_ignored");
        var range = response.Content.Headers.ContentRange;
        var declaredLength = response.Content.Headers.ContentLength;
        var total = range?.Length ?? declaredLength;
        var expectedBytes = maximumBytes;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (range?.From != start || range.To is null || range.To < start || range.Length is null ||
                range.Length <= start || expectedTotal.HasValue && range.Length != expectedTotal.Value)
                throw new ProbeFailure("invalid_range");
            var requestedEnd = checked(start + maximumBytes - 1L);
            var expectedEnd = Math.Min(requestedEnd, range.Length.Value - 1);
            if (range.To != expectedEnd)
                throw new ProbeFailure("invalid_range");
            expectedBytes = checked((int)(range.To.Value - range.From!.Value + 1));
            if (declaredLength.HasValue && declaredLength.Value > expectedBytes)
                throw new ProbeFailure("invalid_range");
        }

        if (declaredLength is >= 0 && response.StatusCode != HttpStatusCode.PartialContent)
            expectedBytes = checked((int)Math.Min(maximumBytes, declaredLength.Value));

        await using var stream = await response.Content.ReadAsStreamAsync(firstByteToken);
        var first = new byte[1];
        var firstRead = await stream.ReadAsync(first.AsMemory(), firstByteToken).AsTask().WaitAsync(firstByteToken);
        if (firstRead == 0) throw new ProbeFailure("empty_body");
        var firstByteLatency = Elapsed(started);
        using var completion = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        completion.CancelAfter(AudioSampleCompletionBudget);
        var targetBytes = response.StatusCode == HttpStatusCode.PartialContent && declaredLength is null
            ? checked(expectedBytes + 1)
            : expectedBytes;
        using var output = new MemoryStream(Math.Min(expectedBytes, 64 * 1024));
        output.Write(first, 0, firstRead);
        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(expectedBytes, 1), 64 * 1024));
        try
        {
            while (output.Length < targetBytes)
            {
                var remaining = checked((int)Math.Min(targetBytes - output.Length, rented.Length));
                var read = await stream.ReadAsync(rented.AsMemory(0, remaining), completion.Token).AsTask()
                    .WaitAsync(completion.Token);
                if (read == 0) break;
                output.Write(rented, 0, read);
                if (response.StatusCode == HttpStatusCode.PartialContent && output.Length > expectedBytes)
                    throw new ProbeFailure("invalid_range");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }

        if (response.StatusCode == HttpStatusCode.PartialContent && output.Length != expectedBytes)
            throw new ProbeFailure("short_body");
        return new AudioRead(checked((int)output.Length), total, firstByteLatency);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, Dictionary<string, string>? query, CancellationToken token)
    {
        using var response = await SendAsync(path, query, null, token);
        EnsureSuccess(response);
        return JsonDocument.Parse(await ReadBoundedAsync(response.Content, MaxJsonBytes, token));
    }

    private async Task<HttpResponseMessage> SendAsync(string path, Dictionary<string, string>? query,
        RangeHeaderValue? range, CancellationToken token, HttpCompletionOption completion = HttpCompletionOption.ResponseHeadersRead)
    {
        var url = new Uri(new Uri(_settings.Url!.TrimEnd('/') + "/"),
            QueryHelpers.AddQueryString(path.TrimStart('/'), query?.ToDictionary(item => item.Key, item => (string?)item.Value)
                ?? new Dictionary<string, string?>()));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(_settings.ApiKey!,
            _settings.ClientName, _settings.DeviceName, _settings.DeviceId, _settings.ClientVersion));
        if (range != null) request.Headers.Range = range;
        var operation = _client.SendAsync(request, completion, token);
        try { return await operation.WaitAsync(token); }
        catch
        {
            _ = DisposeLateResponseAsync(operation);
            throw;
        }
    }

    private static async Task DisposeLateResponseAsync(Task<HttpResponseMessage> operation)
    {
        try
        {
            using var response = await operation.ConfigureAwait(false);
        }
        catch { }
    }

    private async Task<(HealthSmokeStep Step, T? Value)> StepAsync<T>(string name, TimeSpan budget,
        CancellationToken cancellationToken, Func<CancellationToken, Task<T>> action,
        Func<T, double>? latency = null)
    {
        var started = _time.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        try
        {
            var value = await action(timeout.Token);
            return (new(name, "passed", latency?.Invoke(value) ?? Elapsed(started)), value);
        }
        catch (ProbeSkip skip)
        {
            return (new(name, "skipped", Elapsed(started), skip.Reason), default);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new(name, "failed", Elapsed(started), "timeout"), default);
        }
        catch (Exception error)
        {
            return (new(name, "failed", Elapsed(started), error is ProbeFailure failure ? failure.Reason :
                error is JsonException ? "invalid_response" : error is HttpRequestException ? "transport_failure" : "probe_failed"), default);
        }
    }

    private double Elapsed(long started) => Math.Round(_time.GetElapsedTime(started).TotalMilliseconds, 1);

    private static IReadOnlyList<HealthSmokeStep> Unavailable() =>
        StepNames.Select(name => new HealthSmokeStep(name, name == "backend" ? "failed" : "skipped", 0,
            "service_api_key_unavailable")).ToArray();

    private static IReadOnlyList<HealthSmokeStep> AddSkipped(List<HealthSmokeStep> results, string reason)
    {
        var existing = results.Select(step => step.Name).ToHashSet(StringComparer.Ordinal);
        results.AddRange(StepNames.Where(name => !existing.Contains(name)).Select(name => new HealthSmokeStep(name, "skipped", 0, reason)));
        return results;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new ProbeFailure("http_" + (int)response.StatusCode);
    }

    private static bool IsTextualContent(string? mediaType) =>
        mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
        string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(maximumBytes + 1, 64 * 1024));
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, Math.Min(rented.Length, maximumBytes + 1)), token)
                    .AsTask().WaitAsync(token);
                if (read == 0) break;
                if (output.Length + read > maximumBytes) throw new ProbeFailure("response_too_large");
                output.Write(rented, 0, read);
            }
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static bool IsNativeAudio(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Type", out var type) &&
        string.Equals(type.GetString(), "Audio", StringComparison.OrdinalIgnoreCase) &&
        item.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String &&
        !id.GetString()!.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) &&
        !id.GetString()!.StartsWith("external:", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetImageTag(JsonElement item, out string tag)
    {
        tag = string.Empty;
        if (!item.TryGetProperty("ImageTags", out var tags) || tags.ValueKind != JsonValueKind.Object ||
            !tags.TryGetProperty("Primary", out var primary) || primary.ValueKind != JsonValueKind.String)
            return false;
        var value = primary.GetString();
        if (string.IsNullOrWhiteSpace(value)) return false;
        tag = value;
        return true;
    }

    private static bool ContainsLyrics(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return !string.IsNullOrWhiteSpace(value.GetString());
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(ContainsLyrics);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
            if (property.Name is "Text" or "text" or "value" or "line" && ContainsLyrics(property.Value) ||
                property.Name is "Lyrics" or "lyrics" or "LyricsList" or "structuredLyrics" && ContainsLyrics(property.Value))
                return true;
        return false;
    }

    private sealed record SearchResult(string Id, bool Fallback);
    private sealed record Metadata(JsonElement Root);
    private sealed record AudioRead(int Bytes, long? TotalLength, double FirstByteLatencyMilliseconds);
    private sealed class ProbeSkip(string reason) : Exception { public string Reason { get; } = reason; }
    private sealed class ProbeFailure(string reason) : Exception { public string Reason { get; } = reason; }
}
