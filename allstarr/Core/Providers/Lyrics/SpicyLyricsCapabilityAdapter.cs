using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class SpicyLyricsCapabilityAdapter(
    HttpClient http, IProviderAccountSecretAccessor secrets, IApplicationCache? cache = null,
    Func<TimeSpan>? lyricsTtl = null, TimeProvider? clock = null) : IProviderLyricsCapability, IInternalLyricsCapability
{
    public const string StableProviderId = "spicylyrics";
    public const string HttpClientName = "SpicyLyrics";
    private const int MaximumBytes = 2_000_000;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    private TimeProvider Clock => clock ?? TimeProvider.System;
    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    private sealed record CachedLyrics(ProviderLyricsResult Result, DateTimeOffset FetchedAtUtc, DateTimeOffset ExpiresAtUtc);
    private sealed record Loaded(ProviderOutcome<ProviderLyricsResult> Outcome, CachedLyrics? Fresh = null);
    private sealed record Segment(string Text, TimeSpan? Start, TimeSpan? End, bool Background);
    private sealed record Row(string Text, TimeSpan? Start, TimeSpan? End, IReadOnlyList<Segment>? Segments = null);

    ValueTask<LyricsSourceSnapshot> IInternalLyricsCapability.CaptureSourceAsync(CancellationToken token) =>
        ValueTask.FromResult(LyricsAwait.Static(ProviderId, LyricsSpeculationMode.RecentHealthy));
    Task<ProviderOutcome<ProviderLyricsResult>> IInternalLyricsCapability.FetchAsync(ProviderExecutionContext context,
        ProviderLyricsRequest request, LyricsExecution execution) => FetchCoreAsync(context, request, execution.Completion);
    public Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(ProviderExecutionContext context, ProviderLyricsRequest request) =>
        FetchCoreAsync(context, request, LyricsCompletionMode.RequestBound);

    internal async Task<ProviderOutcome<ProviderLyricsResult>> FetchCoreAsync(ProviderExecutionContext context,
        ProviderLyricsRequest request, LyricsCompletionMode completion, bool useCache = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (context.ProviderId != ProviderId || !context.Policy.AllowsProvider(ProviderId) ||
            request.ProviderTrackId.ProviderId != ProviderId || request.ProviderTrackId.ResourceKind != ProviderResourceKind.Track ||
            context.Actor.Kind == ProviderActorKind.PublicRead)
            return Failure(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested) return Failure(ProviderErrorKind.Canceled);
        if (context.IsExpired(Clock.GetUtcNow())) return Failure(ProviderErrorKind.CapabilityUnavailable);
        if (context.Account is not { SecretReferenceId: not null } account)
            return Failure(ProviderErrorKind.AccountNeedsConfiguration);
        if (!SpotifyTrackReference.TryNormalize(request.ProviderTrackId.Value, out var trackId)) return Unavailable();
        var remaining = context.Remaining(Clock.GetUtcNow());
        using var timer = new CancellationTokenSource(remaining < TimeSpan.FromSeconds(6) ? remaining : TimeSpan.FromSeconds(6), Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timer.Token);
        var token = linked.Token;
        var key = CacheKey(context, trackId);
        var wrote = false;
        try
        {
            var loaded = await secrets.UseAsync(account, async bytes =>
            {
                token.ThrowIfCancellationRequested();
                if (!TryKey(bytes, out var apiKey)) return new Loaded(Failure(ProviderErrorKind.AccountNeedsConfiguration));
                if (useCache && cache?.IsEnabled == true)
                {
                    CachedLyrics? cached = null;
                    try { cached = await LyricsAwait.CompleteAsync(cache.GetAsync<CachedLyrics>(key), completion, token); }
                    catch (Exception exception) when (exception is not OperationCanceledException) { }
                    token.ThrowIfCancellationRequested();
                    if (cached is not null && ValidCache(cached, context.Deadline))
                        return new Loaded(ProviderOutcome<ProviderLyricsResult>.Success(cached.Result));
                }
                var fetched = Clock.GetUtcNow();
                var result = await ReadAsync(apiKey, trackId, token);
                token.ThrowIfCancellationRequested();
                var ttl = lyricsTtl?.Invoke() ?? TimeSpan.FromDays(7);
                if (ttl > MaximumAge) ttl = MaximumAge;
                return new Loaded(result, result.IsSuccess && result.RequireValue().Content != null && ttl > TimeSpan.Zero
                    ? new(result.RequireValue(), fetched, fetched.Add(ttl)) : null);
            }, token);
            await AuthorizeAsync(account, token);
            if (loaded.Fresh is { } fresh && useCache && cache?.IsEnabled == true && fresh.ExpiresAtUtc > Clock.GetUtcNow())
            {
                try
                {
                    wrote = true;
                    await LyricsAwait.CompleteAsync(cache.SetAsync(key, fresh, fresh.ExpiresAtUtc - Clock.GetUtcNow()), completion, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { }
                await AuthorizeAsync(account, token);
            }
            token.ThrowIfCancellationRequested();
            return loaded.Outcome.IsSuccess ? Project(loaded.Outcome.RequireValue(), request) : loaded.Outcome;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or KeyNotFoundException)
        {
            if (wrote && cache != null)
            {
                try { await LyricsAwait.CompleteAsync(cache.DeleteAsync(key), completion, token); }
                catch (Exception) { }
            }
            return Failure(ProviderErrorKind.AccountNeedsConfiguration);
        }
        catch (OperationCanceledException)
        {
            return Failure(context.CancellationToken.IsCancellationRequested ? ProviderErrorKind.Canceled :
                context.IsExpired(Clock.GetUtcNow()) ? ProviderErrorKind.CapabilityUnavailable : ProviderErrorKind.TransientFailure);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException or DecoderFallbackException)
        { return ContractFailure(); }
        catch (Exception) { return Failure(ProviderErrorKind.TransientFailure); }
    }

    private async Task AuthorizeAsync(ProviderAccountContext account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await secrets.UseAsync(account, bytes => Task.FromResult(TryKey(bytes, out _) ? true :
            throw new UnauthorizedAccessException()), token);
        token.ThrowIfCancellationRequested();
    }

    private bool ValidCache(CachedLyrics entry, DateTimeOffset deadline)
    {
        var now = Clock.GetUtcNow();
        return entry.Result is { Availability: ProviderLyricsAvailabilityState.Available, Source: StableProviderId, Content: not null } &&
            entry.FetchedAtUtc <= now && entry.ExpiresAtUtc > deadline && entry.ExpiresAtUtc > now &&
            entry.ExpiresAtUtc > entry.FetchedAtUtc && entry.ExpiresAtUtc - entry.FetchedAtUtc <= MaximumAge;
    }

    private static bool TryKey(ReadOnlyMemory<byte> bytes, out string key)
    {
        key = string.Empty;
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        foreach (var item in document.RootElement.EnumerateObject())
            if (item.Name.Equals("apiKey", StringComparison.OrdinalIgnoreCase) && item.Value.ValueKind == JsonValueKind.String)
                key = item.Value.GetString()?.Trim() ?? string.Empty;
        return key.Length is > 6 and <= 8_192 && key.StartsWith("sl_sk_", StringComparison.Ordinal) &&
            !key.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
    }

    private static string CacheKey(ProviderExecutionContext context, string trackId)
    {
        var actor = context.Actor;
        var account = context.Account!;
        var binding = JsonSerializer.Serialize(new
        {
            actor.Kind,
            actor.UserId,
            actor.EffectiveUserId,
            actor.BackendPrincipal,
            actor.ActingForUserId,
            context.ProviderId,
            account.AccountId,
            account.Scope,
            account.OwnerUserId,
            account.Revision,
            account.SecretReferenceId,
            TrackId = trackId,
            Parser = 1
        });
        return "lyrics:v4:spicylyrics:1:" + Hash(binding);
    }

    private async Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(string apiKey, string trackId, CancellationToken token)
    {
        var endpoint = new Uri("https://api.spicylyrics.org/v1/lyrics/" + trackId);
        using var outbound = new HttpRequestMessage(HttpMethod.Get, endpoint);
        outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        outbound.Headers.Accept.ParseAdd("application/json");
        outbound.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
        using var response = await http.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.RequestMessage?.RequestUri != endpoint || response.Headers.Location != null) return ContractFailure();
        if (response.StatusCode == HttpStatusCode.NotFound) return Unavailable();
        if (response.StatusCode == HttpStatusCode.Unauthorized) return Failure(ProviderErrorKind.AccountNeedsReauthentication);
        if (response.StatusCode == HttpStatusCode.Forbidden) return Failure(ProviderErrorKind.AccountNeedsConfiguration);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.RateLimited, RetryAfter(response)));
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            return ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.TransientFailure,
                response.StatusCode == HttpStatusCode.ServiceUnavailable ? RetryAfter(response) : null));
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaximumBytes) return ContractFailure();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (body.Length + read > MaximumBytes) return ContractFailure();
            body.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        return Parse(new UTF8Encoding(false, true).GetString(body.ToArray()), trackId);
    }

    private TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - Clock.GetUtcNow()) ?? TimeSpan.FromSeconds(30);
        return TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, 300));
    }

    private static ProviderOutcome<ProviderLyricsResult> Parse(string json, string trackId)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Status", out var status) ||
            status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) || code != 200 || Text(root, "Type") == null ||
            !root.TryGetProperty("Body", out var body) || body.ValueKind != JsonValueKind.Object || Text(body, "id") != trackId)
            return ContractFailure();
        var type = Text(body, "Type");
        var field = type == "Static" ? "Lines" : "Content";
        if (type is not ("Syllable" or "Line" or "Static") || !body.TryGetProperty(field, out var content) || content.ValueKind != JsonValueKind.Array)
            return ContractFailure();
        ValidateRange(Time(body, "StartTime"), Time(body, "EndTime"));
        var rows = new List<Row>();
        var elements = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (++elements > LyricsTimingLimits.MaximumElements) return ContractFailure();
            if (type == "Static") rows.Add(new(RequiredText(item, "Text"), null, null));
            else
            {
                if (Text(item, "Type") != "Vocal") return ContractFailure();
                if (type == "Line")
                {
                    var start = Time(item, "StartTime");
                    var end = Time(item, "EndTime");
                    ValidateRange(start, end);
                    rows.Add(new(RequiredText(item, "Text"), start, end));
                }
                else
                {
                    if (!item.TryGetProperty("Lead", out var lead)) return ContractFailure();
                    rows.Add(ReadGroup(lead, false, ref elements));
                    if (item.TryGetProperty("Background", out var background))
                    {
                        if (background.ValueKind != JsonValueKind.Array) return ContractFailure();
                        foreach (var group in background.EnumerateArray()) rows.Add(ReadGroup(group, true, ref elements));
                    }
                }
            }
        }
        rows = rows.Where(row => !string.IsNullOrWhiteSpace(row.Text)).ToList();
        if (rows.Count == 0) return Unavailable();
        var timed = type != "Static" && rows.All(row => row.Start.HasValue);
        var words = timed && type == "Syllable" && rows.All(row => row.Segments is { Count: > 0 } && row.Segments.All(segment => segment.Start.HasValue));
        if (timed) rows = rows.OrderBy(row => row.Start).ToList();
        var format = words ? ProviderLyricsFormat.WordTimed : timed ? ProviderLyricsFormat.LineTimed : ProviderLyricsFormat.PlainText;
        IReadOnlyList<ProviderLyricsTimedLine>? lines = timed ? rows.Select(row => new ProviderLyricsTimedLine(row.Text,
            row.Start!.Value, Duration(row.Start, row.End), words ? row.Segments!.Select(segment =>
                new ProviderLyricsTimedSegment(segment.Text, segment.Start!.Value, Duration(segment.Start, segment.End),
                    segment.Background ? true : null)).ToArray() : null)).ToArray() : null;
        var text = string.Join('\n', rows.Select(row => !timed ? row.Text : "[" + Stamp(row.Start!.Value) + "]" +
            (words ? string.Concat(row.Segments!.Select(segment => "<" + Stamp(segment.Start!.Value) + ">" + segment.Text +
                (segment.End is { } end ? "<" + Stamp(end) + ">" : ""))) : row.Text)));
        var attribution = Attribution(body);
        var revision = "sha256:" + Hash(JsonSerializer.Serialize(new { Content = text, TimedLines = lines, Attribution = attribution }));
        return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available,
            StableProviderId, format, text, revision, lines, attribution));
    }

    private static Row ReadGroup(JsonElement group, bool background, ref int elements)
    {
        if (++elements > LyricsTimingLimits.MaximumElements || group.ValueKind != JsonValueKind.Object ||
            !group.TryGetProperty("Syllables", out var syllables) || syllables.ValueKind != JsonValueKind.Array)
            throw new JsonException();
        var segments = new List<Segment>();
        var values = syllables.EnumerateArray().ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            if (++elements > LyricsTimingLimits.MaximumElements) throw new JsonException();
            var syllable = values[index];
            var text = RequiredText(syllable, "Text");
            var join = false;
            if (syllable.TryGetProperty("IsPartOfWord", out var part))
            {
                if (part.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
                join = part.GetBoolean();
            }
            if (!join && index + 1 < values.Length) text += " ";
            var start = Time(syllable, "StartTime");
            var end = Time(syllable, "EndTime");
            ValidateRange(start, end);
            segments.Add(new(text, start, end, background));
        }
        var groupStart = Time(group, "StartTime");
        var groupEnd = Time(group, "EndTime");
        ValidateRange(groupStart, groupEnd);
        return new(string.Concat(segments.Select(segment => segment.Text)), groupStart, groupEnd, segments);
    }

    private static ProviderLyricsAttribution Attribution(JsonElement body)
    {
        var catalog = Text(body, "source") is "spicy_lyrics" or "apple_music" or "spotify" ? Text(body, "source")! : "unknown";
        ProviderLyricsContributor? uploader = null, maker = null;
        if (catalog == "spicy_lyrics" && body.TryGetProperty("UploadAttribution", out var attribution) && attribution.ValueKind == JsonValueKind.Object)
        {
            uploader = Contributor(attribution, "Uploader");
            maker = Contributor(attribution, "Maker");
        }
        return new(StableProviderId, catalog, uploader, maker);
    }

    private static ProviderLyricsContributor? Contributor(JsonElement attribution, string role)
    {
        if (!attribution.TryGetProperty(role, out var person) || person.ValueKind != JsonValueKind.Object) return null;
        var username = Text(person, "username")?.Trim();
        if (string.IsNullOrWhiteSpace(username) || username.Length > 200 || username.Any(char.IsControl)) return null;
        var url = Text(person, "url");
        var id = Text(person, "id");
        if (!ProviderLyricsContributor.IsPublicProfileUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var profile) ||
            profile.AbsolutePath[5..] != id) url = null;
        return new(username, url);
    }

    private static string? Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredText(JsonElement item, string key)
    {
        var text = Text(item, key) ?? throw new JsonException();
        LyricsTimingLimits.ValidateText(text);
        return text;
    }
    private static TimeSpan? Time(JsonElement item, string key)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var seconds) || seconds is < 0 or > 86_400)
            throw new JsonException();
        return TimeSpan.FromTicks(checked((long)decimal.Round(seconds * TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero)));
    }
    private static void ValidateRange(TimeSpan? start, TimeSpan? end)
    {
        if (start.HasValue && end.HasValue && end < start) throw new JsonException();
    }
    private static TimeSpan? Duration(TimeSpan? start, TimeSpan? end) => start.HasValue && end.HasValue ? end - start : null;
    private static string Stamp(TimeSpan value)
    {
        var ms = (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 60_000:00}:{ms / 1_000 % 60:00}.{ms % 1_000:000}");
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ProviderOutcome<ProviderLyricsResult> Project(ProviderLyricsResult result, ProviderLyricsRequest request)
    {
        if (result.Availability != ProviderLyricsAvailabilityState.Available) return ProviderOutcome<ProviderLyricsResult>.Success(result);
        if (request.AvailabilityOnly) return ProviderOutcome<ProviderLyricsResult>.Success(new(result.Availability, result.Source,
            revision: result.Revision, attribution: result.Attribution));
        if (request.PreferredFormat == ProviderLyricsFormat.PlainText && result.Format != ProviderLyricsFormat.PlainText)
            return ProviderOutcome<ProviderLyricsResult>.Success(new(result.Availability, result.Source, ProviderLyricsFormat.PlainText,
                string.Join('\n', result.TimedLines!.Select(line => line.Text)), result.Revision, attribution: result.Attribution));
        return ProviderOutcome<ProviderLyricsResult>.Success(result);
    }
    private static ProviderOutcome<ProviderLyricsResult> Unavailable() => ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, StableProviderId));
    private static ProviderOutcome<ProviderLyricsResult> Failure(ProviderErrorKind kind) => ProviderOutcome<ProviderLyricsResult>.Failure(new(kind));
    private static ProviderOutcome<ProviderLyricsResult> ContractFailure() => ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.CompatibilityContractChanged());

    public static ProviderRegistration CreateRegistration(SpicyLyricsCapabilityAdapter adapter) => new(
        new ProviderDescriptor(StableProviderId, "SpicyLyrics", "Syllable, line, and plain lyrics for known Spotify tracks using your own free server API key.",
            ProviderOrigin.BuiltIn, "1", "spicylyrics-v1",
            [new(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                "1", ["fetchLyrics"], [ProviderAccountScope.Personal, ProviderAccountScope.Shared])],
            new ProviderPermissionDescriptor([new Uri("https://api.spicylyrics.org/")], secretSettingKeys: ["apiKey"]),
            settings: [new("apiKey", ProviderSettingValueKind.Secret, ProviderSettingScope.ProviderAccount, "Server API key", required: true,
                helpText: "Create your own free server key at https://developers.spicylyrics.org/docs/keys. Spotify track links work without a Spotify account.")]),
        [adapter]);
}
