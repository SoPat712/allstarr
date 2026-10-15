using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class MusixmatchLyricsCapabilityAdapter : IProviderLyricsCapability
{
    public const string StableProviderId = "musixmatch";
    public const string HttpClientName = "MusixmatchLyrics";

    private const int MaximumResponseBytes = 2_000_000;
    private const long MaximumTimestampMilliseconds = 24L * 60 * 60 * 1000;
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/86.0.4240.183 Safari/537.36";
    private static readonly Uri DesktopEndpoint =
        new("https://apic-desktop.musixmatch.com/ws/1.1/macro.subtitles.get");
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly HttpClient _http;
    private readonly IProviderAccountSecretAccessor _secrets;
    private readonly IApplicationCache _cache;

    private readonly record struct RichsyncLyrics(
        string Content,
        IReadOnlyList<ProviderLyricsTimedLine>? TimedLines);

    private readonly record struct SelectedLyrics(
        ProviderLyricsFormat Format,
        string Content,
        IReadOnlyList<ProviderLyricsTimedLine>? TimedLines);

    public MusixmatchLyricsCapabilityAdapter(
        HttpClient http,
        IProviderAccountSecretAccessor secrets,
        IApplicationCache cache)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public string ProviderId => StableProviderId;

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    internal static async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsFromTokenAsync(
        HttpClient http,
        string userToken,
        ProviderLyricsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ReadLyricsFromTokenCoreAsync(
            http ?? throw new ArgumentNullException(nameof(http)),
            userToken,
            request ?? throw new ArgumentNullException(nameof(request)),
            cancellationToken);
        return result.IsSuccess
            ? Project(result.RequireValue(), request)
            : result;
    }

    public async Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
        ProviderExecutionContext context,
        ProviderLyricsRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (!context.ProviderId.Equals(StableProviderId, StringComparison.Ordinal) ||
                !context.Policy.AllowsProvider(StableProviderId))
                return Failure<ProviderLyricsResult>(ProviderErrorKind.Forbidden);

            try
            {
                context.RequireResourceOwner(request.ProviderTrackId, ProviderResourceKind.Track);
            }
            catch (ArgumentException)
            {
                return Failure<ProviderLyricsResult>(ProviderErrorKind.Forbidden);
            }

            if (context.Actor.Kind == ProviderActorKind.PublicRead)
                return Failure<ProviderLyricsResult>(ProviderErrorKind.Forbidden);
            if (context.CancellationToken.IsCancellationRequested)
                return Failure<ProviderLyricsResult>(ProviderErrorKind.Canceled);
            if (context.IsExpired(DateTimeOffset.UtcNow))
                return Failure<ProviderLyricsResult>(ProviderErrorKind.CapabilityUnavailable);
            if (context.Account == null)
                return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsConfiguration);
            if (context.Account.SecretReferenceId == null)
                return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsConfiguration);

            using var operation = StartOperation(context);
            var cacheKey = BuildCacheKey(context.Account, request);
            var cached = await ReadCacheAsync(cacheKey, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (cached?.Availability == ProviderLyricsAvailabilityState.Available &&
                cached.Content != null)
                return Project(cached, request);

            var loaded = await _secrets.UseAsync(
                context.Account,
                secret =>
                {
                    operation.Token.ThrowIfCancellationRequested();
                    return TryReadUserToken(secret, out var userToken)
                        ? ReadLyricsFromTokenCoreAsync(
                            _http,
                            userToken,
                            request,
                            operation.Token)
                        : Task.FromResult(Failure<ProviderLyricsResult>(
                            ProviderErrorKind.AccountNeedsConfiguration));
                },
                operation.Token);
            if (!loaded.IsSuccess)
            {
                if (loaded.Error?.Kind == ProviderErrorKind.Canceled &&
                    !context.CancellationToken.IsCancellationRequested &&
                    context.IsExpired(DateTimeOffset.UtcNow))
                    return Failure<ProviderLyricsResult>(
                        ProviderErrorKind.CapabilityUnavailable);
                return loaded;
            }

            operation.Token.ThrowIfCancellationRequested();
            var result = loaded.RequireValue();
            if (result.Availability == ProviderLyricsAvailabilityState.Available &&
                result.Content != null)
                await WriteCacheAsync(cacheKey, result, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            return Project(result, request);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.Canceled);
        }
        catch (OperationCanceledException)
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.CapabilityUnavailable);
        }
        catch (KeyNotFoundException)
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsConfiguration);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsConfiguration);
        }
        catch (HttpRequestException)
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.TransientFailure);
        }
        catch
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.TransientFailure);
        }
    }

    public static ProviderRegistration CreateRegistration(
        MusixmatchLyricsCapabilityAdapter adapter) => new(
        new ProviderDescriptor(
            StableProviderId,
            "Musixmatch",
            "Account-bound Musixmatch desktop lyrics with precise plain and timed content.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "musixmatch-desktop-lyrics-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Lyrics,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    "1",
                    ["fetchLyrics"],
                    [ProviderAccountScope.Personal, ProviderAccountScope.Shared])
            ],
            permissions: new ProviderPermissionDescriptor(
                [new Uri("https://apic-desktop.musixmatch.com/")],
                cache: false,
                secretSettingKeys: ["userToken"]),
            settings:
            [
                new ProviderSettingDescriptor(
                    "userToken",
                    ProviderSettingValueKind.Secret,
                    ProviderSettingScope.ProviderAccount,
                    "Desktop user token",
                    required: true,
                    helpText: "Copy the user token from the Musixmatch desktop app's debug information. Allstarr encrypts it.")
            ]),
        [adapter]);

    private static bool TryReadUserToken(
        ReadOnlyMemory<byte> secret,
        out string token)
    {
        token = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(
                StrictUtf8.GetString(secret.Span),
                new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("userToken", StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.String)
                    continue;
                var value = property.Value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 4_000)
                    return false;
                token = value;
                return true;
            }
        }
        catch (JsonException)
        {
        }
        catch (DecoderFallbackException)
        {
        }

        return false;
    }

    private static async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsFromTokenCoreAsync(
        HttpClient http,
        string userToken,
        ProviderLyricsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.ProviderTrackId.ProviderId != StableProviderId ||
                request.ProviderTrackId.ResourceKind != ProviderResourceKind.Track)
                return Failure<ProviderLyricsResult>(ProviderErrorKind.Forbidden);
            if (string.IsNullOrWhiteSpace(userToken) || userToken.Length > 4_000)
                return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsConfiguration);
            userToken = userToken.Trim();
            cancellationToken.ThrowIfCancellationRequested();

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Get,
                BuildEndpoint(request, userToken));
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            httpRequest.Headers.TryAddWithoutValidation("Accept-Language", "en");
            httpRequest.Headers.TryAddWithoutValidation("Cookie", "AWSELBCORS=0; AWSELB=0");
            httpRequest.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var httpStatus = (int)response.StatusCode;
            if (httpStatus == 404)
                return Unavailable();
            var httpError = ErrorForStatus(httpStatus, RetryAfter(response));
            if (httpError != null)
                return Failure<ProviderLyricsResult>(httpError);

            var body = await ReadBoundedBodyAsync(response.Content, cancellationToken);
            if (body == null)
                return Failure<ProviderLyricsResult>(
                    ProviderError.CompatibilityContractChanged());
            return ParseMacro(body, request);
        }
        catch (OperationCanceledException)
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.Canceled);
        }
        catch (JsonException)
        {
            return Failure<ProviderLyricsResult>(
                ProviderError.CompatibilityContractChanged());
        }
        catch (DecoderFallbackException)
        {
            return Failure<ProviderLyricsResult>(
                ProviderError.CompatibilityContractChanged());
        }
        catch (HttpRequestException)
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.TransientFailure);
        }
        catch
        {
            return Failure<ProviderLyricsResult>(ProviderErrorKind.TransientFailure);
        }
    }

    private static Uri BuildEndpoint(
        ProviderLyricsRequest request,
        string userToken)
    {
        var artist = string.Join(", ", request.ArtistNames);
        var track = request.TrackTitle ?? request.ProviderTrackId.Value;
        var values = new List<KeyValuePair<string, string>>
        {
            new("app_id", "web-desktop-app-v1.0"),
            new("format", "json"),
            new("namespace", "lyrics_richsynced"),
            new("optional_calls", "track.richsync"),
            new("subtitle_format", "lrc"),
            new("q_artist", artist),
            new("q_track", track),
            new("usertoken", userToken),
            new("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture))
        };
        if (request.DurationSeconds is > 0)
        {
            var duration = request.DurationSeconds.Value;
            values.Add(new(
                "f_subtitle_length",
                duration.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            values.Add(new("f_subtitle_length_max_deviation", "3"));
        }

        var query = string.Join(
            "&",
            values.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        return new UriBuilder(DesktopEndpoint) { Query = query }.Uri;
    }

    private static async Task<string?> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (body.Length + read > MaximumResponseBytes)
                return null;
            body.Write(buffer, 0, read);
        }

        return StrictUtf8.GetString(body.ToArray());
    }

    private static ProviderOutcome<ProviderLyricsResult> ParseMacro(
        string json,
        ProviderLyricsRequest request)
    {
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (!TryGetObject(root, "message", out var envelope) ||
            !TryGetStatus(envelope, out var outerStatus))
            return ContractFailure();

        if (outerStatus == 404)
            return Unavailable();
        var outerError = ErrorForStatus(outerStatus, retryAfter: null);
        if (outerError != null)
            return Failure<ProviderLyricsResult>(outerError);
        if (!TryGetObject(envelope, "body", out var body) ||
            !TryGetObject(body, "macro_calls", out var calls))
            return ContractFailure();
        if (!calls.TryGetProperty("matcher.track.get", out var matcherCall))
            return Unavailable();

        if (!TryGetCallBody(matcherCall, out var matcherBody, out var matcherError))
            return matcherError == null
                ? Unavailable()
                : Failure<ProviderLyricsResult>(matcherError);
        if (!TryReadTrack(matcherBody, out var metadata) ||
            !Matches(request, metadata))
            return Unavailable();

        ProviderError? deferredError = null;
        var timed = ReadRichsync(calls, "track.richsync.get", out var timedError);
        deferredError ??= timedError;
        var subtitle = ReadSubtitle(calls, "track.subtitles.get", out var subtitleError);
        deferredError ??= subtitleError;
        var plain = ReadPlain(calls, "track.lyrics.get", out var plainError);
        deferredError ??= plainError;

        var selected = SelectContent(request.PreferredFormat, timed, subtitle, plain);
        return selected != null
            ? Available(selected.Value.Format, selected.Value.Content, selected.Value.TimedLines)
            : deferredError == null
                ? Unavailable()
                : Failure<ProviderLyricsResult>(deferredError);
    }

    private static SelectedLyrics? SelectContent(
        ProviderLyricsFormat? preferred,
        RichsyncLyrics? timed,
        string? subtitle,
        string? plain)
    {
        var synced = timed?.Content ?? subtitle;
        if (preferred == ProviderLyricsFormat.PlainText)
        {
            if (!string.IsNullOrWhiteSpace(plain))
                return new(ProviderLyricsFormat.PlainText, plain, timed?.TimedLines);
            if (!string.IsNullOrWhiteSpace(synced))
                return new(ProviderLyricsFormat.PlainText, StripTimestamps(synced), timed?.TimedLines);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(synced))
            return new(ProviderLyricsFormat.LineTimed, synced, timed?.TimedLines);
        return string.IsNullOrWhiteSpace(plain)
            ? null
            : new(ProviderLyricsFormat.PlainText, plain, null);
    }

    private static string StripTimestamps(string lyrics) =>
        string.Join('\n', MusicLyrics.ParseSyncedLines(lyrics).Select(line => line.Text));

    private static RichsyncLyrics? ReadRichsync(
        JsonElement calls,
        string name,
        out ProviderError? error)
    {
        error = null;
        if (!TryGetCallBody(calls, name, out var body, out error))
            return null;
        try
        {
            if (!TryGetObject(body, "richsync", out var richsync) ||
                !TryGetString(richsync, "richsync_body", out var raw) ||
                string.IsNullOrWhiteSpace(raw))
                return null;
            using var linesDocument = JsonDocument.Parse(
                raw!,
                new JsonDocumentOptions { MaxDepth = 16 });
            if (linesDocument.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = ProviderError.CompatibilityContractChanged();
                return null;
            }

            var rendered = new StringBuilder();
            var timedLines = new List<ProviderLyricsTimedLine>();
            var hasSegments = false;
            var invalidTiming = false;
            foreach (var line in linesDocument.RootElement.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object ||
                    !line.TryGetProperty("ts", out var timestampValue) ||
                    !TryReadSeconds(timestampValue, out var timestamp) ||
                    !TryGetString(line, "x", out var text) ||
                    string.IsNullOrEmpty(text) ||
                    text.Contains('\r') || text.Contains('\n'))
                {
                    error = ProviderError.CompatibilityContractChanged();
                    return null;
                }

                TimeSpan? duration = null;
                if (line.TryGetProperty("te", out var endValue))
                {
                    if (!TryReadSeconds(endValue, out var end) || end < timestamp)
                    {
                        invalidTiming = true;
                    }
                    else
                    {
                        duration = ToSourceTime(end) - ToSourceTime(timestamp);
                    }
                }

                IReadOnlyList<ProviderLyricsTimedSegment>? segments = null;
                if (line.TryGetProperty("l", out var nested))
                {
                    if (nested.ValueKind != JsonValueKind.Array ||
                        !TryReadRichsyncSegments(nested, timestamp, out var parsedSegments))
                    {
                        invalidTiming = true;
                    }
                    else
                    {
                        segments = parsedSegments;
                        hasSegments |= parsedSegments.Count != 0;
                    }
                }

                timedLines.Add(new(
                    text,
                    ToSourceTime(timestamp),
                    duration,
                    segments));
                if (rendered.Length != 0)
                    rendered.Append('\n');
                rendered.Append(FormatTimestamp(timestamp, text));
            }

            return rendered.Length == 0
                ? null
                : new(
                    rendered.ToString(),
                    hasSegments && !invalidTiming ? timedLines : null);
        }
        catch (JsonException)
        {
            error = ProviderError.CompatibilityContractChanged();
            return null;
        }
        catch (ArgumentException)
        {
            error = ProviderError.CompatibilityContractChanged();
            return null;
        }
    }

    private static bool TryReadRichsyncSegments(
        JsonElement nested,
        decimal lineStart,
        out IReadOnlyList<ProviderLyricsTimedSegment> segments)
    {
        if (nested.GetArrayLength() > LyricsTimingLimits.MaximumElements)
        {
            segments = [];
            return false;
        }
        var parsed = new List<ProviderLyricsTimedSegment>();
        foreach (var chunk in nested.EnumerateArray())
        {
            if (chunk.ValueKind != JsonValueKind.Object ||
                !TryGetString(chunk, "c", out var text) ||
                text == null ||
                !chunk.TryGetProperty("o", out var offsetValue) ||
                offsetValue.ValueKind != JsonValueKind.Number ||
                !offsetValue.TryGetDecimal(out var offset) ||
                offset < -86_400m || offset > 86_400m)
            {
                segments = [];
                return false;
            }

            var absolute = lineStart + offset;
            if (absolute < 0 ||
                absolute > MaximumTimestampMilliseconds / 1000m ||
                text.Contains('\r') || text.Contains('\n'))
            {
                segments = [];
                return false;
            }

            parsed.Add(new(text, ToSourceTime(absolute)));
        }

        segments = parsed;
        return true;
    }

    private static bool TryReadSeconds(JsonElement value, out decimal seconds)
    {
        seconds = 0;
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetDecimal(out seconds) &&
               seconds >= 0 &&
               seconds <= MaximumTimestampMilliseconds / 1000m;
    }

    private static TimeSpan ToSourceTime(decimal seconds) => TimeSpan.FromTicks(
        decimal.ToInt64(decimal.Round(seconds * TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero)));

    private static string FormatTimestamp(decimal timestamp, string text)
    {
        var milliseconds = Math.Round(
            timestamp * 1000m,
            MidpointRounding.AwayFromZero);
        var elapsed = TimeSpan.FromTicks((long)milliseconds * TimeSpan.TicksPerMillisecond);
        return FormattableString.Invariant(
            $"[{(long)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds:000}]{text}");
    }

    private static string? ReadSubtitle(
        JsonElement calls,
        string name,
        out ProviderError? error)
    {
        error = null;
        if (!TryGetCallBody(calls, name, out var body, out error))
            return null;
        if (!body.TryGetProperty("subtitle_list", out var subtitles) ||
            subtitles.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in subtitles.EnumerateArray())
        {
            if (TryGetObject(item, "subtitle", out var subtitle) &&
                TryGetString(subtitle, "subtitle_body", out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                var lines = MusicLyrics.ParseSyncedLines(value);
                if (lines.Count > 0 && lines.All(line => line.StartMilliseconds <= MaximumTimestampMilliseconds))
                    return value;
                error = ProviderError.CompatibilityContractChanged();
            }
        }
        return null;
    }

    private static string? ReadPlain(
        JsonElement calls,
        string name,
        out ProviderError? error)
    {
        error = null;
        if (!TryGetCallBody(calls, name, out var body, out error))
            return null;
        return TryGetObject(body, "lyrics", out var lyrics) &&
               TryGetString(lyrics, "lyrics_body", out var value)
            ? value
            : null;
    }

    private static bool TryGetCallBody(
        JsonElement calls,
        string name,
        out JsonElement body,
        out ProviderError? error)
    {
        body = default;
        error = null;
        if (!calls.TryGetProperty(name, out var call))
            return false;
        return TryGetCallBody(call, out body, out error);
    }

    private static bool TryGetCallBody(
        JsonElement call,
        out JsonElement body,
        out ProviderError? error)
    {
        body = default;
        error = null;
        if (!TryGetObject(call, "message", out var message) ||
            !TryGetStatus(message, out var status))
            return false;
        var statusError = ErrorForStatus(status, retryAfter: null);
        if (statusError != null)
        {
            error = status == 404 ? null : statusError;
            return false;
        }
        if (status != 200 || !TryGetObject(message, "body", out body))
            return false;
        return true;
    }

    private static bool TryGetStatus(JsonElement message, out int status)
    {
        status = 0;
        return TryGetObject(message, "header", out var header) &&
               header.TryGetProperty("status_code", out var value) &&
               value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out status);
    }

    private static bool TryReadTrack(
        JsonElement body,
        out TrackMetadata metadata)
    {
        metadata = default;
        if (!TryGetObject(body, "track", out var track))
            return false;
        metadata = new(
            ReadString(track, "artist_name") ?? string.Empty,
            ReadString(track, "track_name") ?? string.Empty,
            ReadString(track, "album_name") ?? string.Empty);
        return true;
    }

    private static bool Matches(
        ProviderLyricsRequest request,
        TrackMetadata actual)
    {
        if (string.IsNullOrWhiteSpace(actual.Artist) ||
            string.IsNullOrWhiteSpace(actual.Title))
            return false;
        if (request.TrackTitle is { Length: > 0 } title &&
            FuzzyMatcher.CalculateSimilarity(title, actual.Title) < 90)
            return false;
        if (request.ArtistNames.Count > 0 &&
            !request.ArtistNames.Any(artist =>
                FuzzyMatcher.CalculateSimilarity(artist, actual.Artist) >= 90))
            return false;
        return true;
    }

    private static bool TryGetObject(
        JsonElement parent,
        string name,
        out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetString(
        JsonElement parent,
        string name,
        out string? value)
    {
        value = ReadString(parent, name);
        return value != null;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<ProviderLyricsResult?> ReadCacheAsync(
        string key,
        CancellationToken cancellationToken)
    {
        if (!_cache.IsEnabled)
            return null;
        try
        {
            var value = await _cache.GetAsync<ProviderLyricsResult>(key)
                .WaitAsync(cancellationToken);
            return value?.Availability == ProviderLyricsAvailabilityState.Available &&
                   value.Content != null
                ? value
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(
        string key,
        ProviderLyricsResult result,
        CancellationToken cancellationToken)
    {
        if (!_cache.IsEnabled ||
            result.Availability != ProviderLyricsAvailabilityState.Available ||
            result.Content == null)
            return;
        try
        {
            await _cache.SetAsync(key, result, CacheExtensions.LyricsTTL)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private static CancellationTokenSource StartOperation(
        ProviderExecutionContext context)
    {
        var operation = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken);
        var remaining = context.Remaining(DateTimeOffset.UtcNow);
        if (remaining <= TimeSpan.Zero)
        {
            operation.Cancel();
            return operation;
        }

        operation.CancelAfter(
            remaining > TimeSpan.FromMilliseconds(int.MaxValue - 1)
                ? TimeSpan.FromMilliseconds(int.MaxValue - 1)
                : remaining);
        return operation;
    }

    private static string BuildCacheKey(
        ProviderAccountContext account,
        ProviderLyricsRequest request)
    {
        var format = request.PreferredFormat?.ToString() ?? "auto";
        var metadata = string.Join(
            "\n",
            [
                request.ProviderTrackId.ProviderId,
                request.ProviderTrackId.ResourceKind.ToString(),
                request.ProviderTrackId.Value,
                request.TrackTitle ?? string.Empty,
                string.Join("\u001f", request.ArtistNames),
                request.AlbumTitle ?? string.Empty,
                request.DurationSeconds?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            ]);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(metadata))).ToLowerInvariant();
        return $"lyrics:v3:{StableProviderId}:{account.AccountId:N}:{account.Revision}:" +
            $"{account.SecretReferenceId?.ToString("N") ?? "none"}:{fingerprint}:" +
            $"{format.ToLowerInvariant()}";
    }

    private static ProviderOutcome<ProviderLyricsResult> Project(
        ProviderLyricsResult result,
        ProviderLyricsRequest request)
    {
        if (request.AvailabilityOnly && result.Availability == ProviderLyricsAvailabilityState.Available)
            return ProviderOutcome<ProviderLyricsResult>.Success(new(
                ProviderLyricsAvailabilityState.Available,
                result.Source,
                revision: result.Revision));
        if (request.PreferredFormat == ProviderLyricsFormat.PlainText && result.TimedLines != null)
            return ProviderOutcome<ProviderLyricsResult>.Success(new(
                result.Availability,
                result.Source,
                result.Format,
                result.Content,
                result.Revision));
        return ProviderOutcome<ProviderLyricsResult>.Success(result);
    }

    private static ProviderOutcome<ProviderLyricsResult> Available(
        ProviderLyricsFormat format,
        string content,
        IReadOnlyList<ProviderLyricsTimedLine>? timedLines)
    {
        try
        {
            var revisionInput = timedLines == null
                ? content
                : JsonSerializer.Serialize(new { Content = content, TimedLines = timedLines });
            var revision = "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput))).ToLowerInvariant();
            return ProviderOutcome<ProviderLyricsResult>.Success(new(
                ProviderLyricsAvailabilityState.Available,
                StableProviderId,
                format,
                content,
                revision,
                timedLines));
        }
        catch (ArgumentException)
        {
            return timedLines == null ? ContractFailure() : Available(format, content, null);
        }
    }

    private static ProviderOutcome<ProviderLyricsResult> Unavailable() =>
        ProviderOutcome<ProviderLyricsResult>.Success(new(
            ProviderLyricsAvailabilityState.Unavailable,
            StableProviderId));

    private static ProviderOutcome<ProviderLyricsResult> ContractFailure() =>
        Failure<ProviderLyricsResult>(ProviderError.CompatibilityContractChanged());

    private static ProviderOutcome<T> Failure<T>(
        ProviderErrorKind kind) =>
        ProviderOutcome<T>.Failure(new(kind));

    private static ProviderOutcome<T> Failure<T>(ProviderError error) =>
        ProviderOutcome<T>.Failure(error);

    private static ProviderError? ErrorForStatus(
        int status,
        TimeSpan? retryAfter)
    {
        if (status is 200 or 404)
            return null;
        if (status == 401)
            return new(ProviderErrorKind.Unauthorized);
        if (status == 403)
            return new(ProviderErrorKind.Forbidden);
        if (status == 429)
            return new(
                ProviderErrorKind.RateLimited,
                retryAfter ?? TimeSpan.FromSeconds(30));
        if (status is >= 500 and <= 599)
            return new(ProviderErrorKind.TransientFailure);
        return ProviderError.CompatibilityContractChanged();
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
            return delta < TimeSpan.Zero
                ? TimeSpan.Zero
                : delta > TimeSpan.FromHours(1)
                    ? TimeSpan.FromHours(1)
                    : delta;
        if (retry?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
                return TimeSpan.Zero;
            return remaining > TimeSpan.FromHours(1)
                ? TimeSpan.FromHours(1)
                : remaining;
        }
        return null;
    }

    private readonly record struct TrackMetadata(
        string Artist,
        string Title,
        string Album);
}
