using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using allstarr.Services.MusicBrainz;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Core.Providers.Lyrics;

public sealed class MusixmatchLyricsCapabilityAdapter : IProviderLyricsCapability
{
    public const string StableProviderId = "musixmatch";
    public const string HttpClientName = "MusixmatchLyrics";

    private const int MaximumResponseBytes = 2_000_000;
    private const long MaximumTimestampMilliseconds = 24L * 60 * 60 * 1000;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly HttpClient _http;
    private readonly IProviderAccountSecretAccessor _secrets;
    private readonly IApplicationCache _cache;
    private readonly ILogger _logger;
    private readonly MusixmatchTokenProvider _automaticTokens;

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
        IApplicationCache cache,
        ILogger<MusixmatchLyricsCapabilityAdapter>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _automaticTokens = new(_http);
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? NullLogger<MusixmatchLyricsCapabilityAdapter>.Instance;
    }

    public string ProviderId => StableProviderId;

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    internal static async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsFromTokenAsync(
        HttpClient http,
        string userToken,
        ProviderLyricsRequest request,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        if (!MusixmatchCredential.TryParse(userToken, out var credential))
            return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsConfiguration);
        return await ReadLyricsFromCredentialAsync(http, credential, request, cancellationToken, logger);
    }

    internal static async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsFromCredentialAsync(
        HttpClient http, MusixmatchCredential credential, ProviderLyricsRequest request,
        CancellationToken cancellationToken, ILogger? logger = null)
    {
        var result = await ReadLyricsFromTokenCoreAsync(http, credential, request, cancellationToken, logger ?? NullLogger.Instance);
        return result.IsSuccess ? Project(result.RequireValue(), request) : result;
    }

    internal Task<ProviderOutcome<ProviderLyricsResult>> ReadAutomaticLyricsAsync(
        ProviderLyricsRequest request, CancellationToken cancellationToken) =>
        ReadAutomaticLyricsAsync(_http, _automaticTokens, request, cancellationToken, _logger);

    internal static async Task<ProviderOutcome<ProviderLyricsResult>> ReadAutomaticLyricsAsync(
        HttpClient http, MusixmatchTokenProvider tokens, ProviderLyricsRequest request,
        CancellationToken cancellationToken, ILogger logger)
    {
        string? rejected = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await tokens.GetAsync(rejected, cancellationToken);
            if (!token.IsSuccess) return Failure<ProviderLyricsResult>(token.Error!);
            var credential = token.RequireValue();
            var result = await ReadLyricsFromTokenCoreAsync(http, credential, request, cancellationToken, logger);
            if (result.Error?.Kind != ProviderErrorKind.AccountNeedsReauthentication) return result;
            rejected = credential.UserToken;
        }
        tokens.Reject(rejected!);
        return Failure<ProviderLyricsResult>(new ProviderError(ProviderErrorKind.RateLimited, TimeSpan.FromMinutes(5)));
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
            if (context.Account != null && context.Account.SecretReferenceId == null)
                return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsConfiguration);

            using var operation = StartOperation(context);
            var cacheKey = BuildCacheKey(context.Account, request);
            var cached = await ReadCacheAsync(cacheKey, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (cached?.Availability == ProviderLyricsAvailabilityState.Available &&
                cached.Content != null)
                return Project(cached, request);

            var loaded = context.Account == null
                ? await ReadAutomaticLyricsAsync(request, operation.Token)
                : await _secrets.UseAsync(
                context.Account,
                secret =>
                {
                    operation.Token.ThrowIfCancellationRequested();
                    return MusixmatchCredential.TryRead(secret, out var credential)
                        ? ReadLyricsFromTokenCoreAsync(
                            _http,
                            credential,
                            request,
                            operation.Token,
                            _logger)
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
            "Musixmatch mobile lyrics with automatic access and an optional app connection.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "musixmatch-mobile-lyrics-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Lyrics,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Optional,
                    "1",
                    ["fetchLyrics"],
                    [ProviderAccountScope.Personal, ProviderAccountScope.Shared])
            ],
            permissions: new ProviderPermissionDescriptor(
                [new Uri("https://apic-appmobile.musixmatch.com/")],
                cache: false,
                secretSettingKeys: ["userToken"]),
            settings:
            [
                new ProviderSettingDescriptor(
                    "userToken",
                    ProviderSettingValueKind.Secret,
                    ProviderSettingScope.ProviderAccount,
                    "Debug Info or user token",
                    required: false,
                    helpText: "Paste the Debug Info from the Musixmatch app, or just the user token. Optional: Musixmatch works without it.",
                    multiline: true)
            ]),
        [adapter]);

    private static Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsFromTokenCoreAsync(
        HttpClient http, MusixmatchCredential credential, ProviderLyricsRequest request,
        CancellationToken cancellationToken, ILogger logger) =>
        ReadLookupAsync(http, credential, request, cancellationToken, logger, new LookupDiagnostics());

    private static async Task<ProviderOutcome<ProviderLyricsResult>> ReadLookupAsync(
        HttpClient http,
        MusixmatchCredential credential,
        ProviderLyricsRequest request,
        CancellationToken cancellationToken,
        ILogger logger,
        LookupDiagnostics diagnostics)
    {
        int? httpStatus = null;
        var sent = false;
        try
        {
            if (request.ProviderTrackId.ProviderId != StableProviderId ||
                request.ProviderTrackId.ResourceKind != ProviderResourceKind.Track)
                return Failure<ProviderLyricsResult>(ProviderErrorKind.Forbidden);
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MusixmatchMobileApi.RequestTimeout);
            using var httpRequest = MusixmatchMobileApi.CreateRequest(BuildEndpoint(request, credential.UserToken), credential);
            sent = true;
            using var response = await http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            httpStatus = (int)response.StatusCode;
            if (httpStatus == 404)
            {
                diagnostics.Decision = "http-not-found";
                return Unavailable();
            }
            var httpError = ErrorForStatus(httpStatus.Value, RetryAfter(response));
            if (httpError != null)
            {
                diagnostics.Decision = "http-error";
                return Failure<ProviderLyricsResult>(httpError);
            }

            var body = await ReadBoundedBodyAsync(response.Content, timeout.Token);
            if (body == null)
                return Failure<ProviderLyricsResult>(
                    ProviderError.CompatibilityContractChanged());
            return ParseMacro(body, request, diagnostics);
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
        finally
        {
            if (sent)
            {
                if (diagnostics.RejectedTrack is { } rejected && TryGetObject(rejected, "track", out var track))
                    logger.LogInformation(
                        "Musixmatch identity-rejected: requested-title {RequestedTitle}; requested-artist {RequestedArtist}; " +
                        "matched-title {MatchedTitle}; matched-artist {MatchedArtist}; matched-album {MatchedAlbum}; track-id {TrackId}; commontrack-id {CommontrackId}",
                        request.TrackTitle, string.Join(", ", request.ArtistNames),
                        ReadString(track, "track_name"), ReadString(track, "artist_name"), ReadString(track, "album_name"),
                        ReadTrackId(track, "track_id"), ReadTrackId(track, "commontrack_id"));
                logger.LogInformation(
                    "Musixmatch lookup {Path}: HTTP {HttpStatus}; envelope {Envelope}; matcher.track.get {Matcher}; track.lyrics.get {Lyrics}; track.subtitles.get {Subtitles}; track.richsync.get {Richsync}; " +
                    "matcher-body {MatcherBody}; track {TrackPresent}; track-keys {TrackKeys}; title-score {TitleScore}; artist-score {ArtistScore}; has-lyrics {HasLyrics}; has-subtitles {HasSubtitles}; instrumental {Instrumental}; restricted {Restricted}; " +
                    "lyrics-body {LyricsBody}; lyrics-body-present {LyricsBodyPresent}; subtitle-body {SubtitleBody}; subtitle-count {SubtitleCount}; subtitle-first {SubtitleStartClass}; richsync-body {RichsyncBody}; richsync-body-present {RichsyncBodyPresent}; " +
                    "parsed-lines plain {PlainLines}/subtitle {SubtitleLines}/richsync {RichsyncLines}; decision {Decision}; lookup-path {LookupPath}; stage {Stage}",
                    MusixmatchMobileApi.LyricsEndpoint.GetLeftPart(UriPartial.Path), httpStatus,
                    diagnostics.Envelope, diagnostics.Matcher, diagnostics.Lyrics, diagnostics.Subtitles, diagnostics.Richsync,
                    diagnostics.MatcherBody, diagnostics.TrackPresent, diagnostics.TrackKeys, diagnostics.TitleScore, diagnostics.ArtistScore,
                    diagnostics.HasLyrics, diagnostics.HasSubtitles, diagnostics.Instrumental, diagnostics.Restricted,
                    diagnostics.LyricsBody, diagnostics.LyricsBodyPresent, diagnostics.SubtitleBody, diagnostics.SubtitleCount,
                    diagnostics.SubtitleFirst, diagnostics.RichsyncBody, diagnostics.RichsyncBodyPresent,
                    diagnostics.PlainLines, diagnostics.SubtitleLines, diagnostics.RichsyncLines, diagnostics.Decision,
                    "matcher", "matcher");
            }
        }
    }

    private static Uri BuildEndpoint(ProviderLyricsRequest request, string userToken)
    {
        var artist = string.Join(", ", request.ArtistNames);
        var values = new List<KeyValuePair<string, string>>
        {
            new("app_id", MusixmatchMobileApi.AppId),
            new("format", "json"),
            new("namespace", "lyrics_richsynched"),
            new("subtitle_format", "mxm"),
            new("usertoken", userToken),
            new("q_artist", artist),
            new("q_artists", artist),
            new("q_album", request.AlbumTitle ?? string.Empty),
            new("q_track", request.TrackTitle ?? request.ProviderTrackId.Value),
            new("optional_calls", "track.richsync"),
            new("richsync_compact_type", "words"),
            new("part", "track_lyrics_translation_status,track_structure,track_performer_tagging")
        };
        if (request.DurationSeconds is > 0)
        {
            var duration = request.DurationSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values.Add(new("q_duration", duration));
            values.Add(new("f_subtitle_length", duration));
        }
        if (MusicBrainzService.NormalizeIsrc(request.Isrc) is { } isrc)
            values.Add(new("q_track_isrc", isrc));
        if (request.ProviderTrackId.Value.StartsWith("spotify:track:", StringComparison.Ordinal) &&
            request.ProviderTrackId.Value[14..] is { Length: 22 } spotifyId && spotifyId.All(char.IsAsciiLetterOrDigit))
            values.Add(new("track_spotify_id", request.ProviderTrackId.Value));
        var query = string.Join("&", values.Where(item => !string.IsNullOrWhiteSpace(item.Value)).Select(item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        return new UriBuilder(MusixmatchMobileApi.LyricsEndpoint) { Query = query }.Uri;
    }

    private static string? ReadTrackId(JsonElement track, string name)
    {
        if (!track.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() :
            value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        return long.TryParse(text, out var id) && id > 0
            ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    internal static async Task<string?> ReadBoundedBodyAsync(
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
        ProviderLyricsRequest request,
        LookupDiagnostics diagnostics)
    {
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        diagnostics.Read(root);
        diagnostics.Decision = "no-content-parsed";
        if (!TryGetObject(root, "message", out var envelope) ||
            !TryGetStatus(envelope, out var outerStatus))
            return ContractFailure();

        if (NeedsReauthentication(envelope))
            return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsReauthentication);
        if (outerStatus == 404)
        {
            return Unavailable();
        }
        var outerError = ErrorForStatus(outerStatus, retryAfter: null);
        if (outerError != null)
            return Failure<ProviderLyricsResult>(outerError);
        if (!TryGetObject(envelope, "body", out var body) ||
            !TryGetObject(body, "macro_calls", out var calls))
            return ContractFailure();
        foreach (var call in calls.EnumerateObject())
        {
            if (TryGetObject(call.Value, "message", out var message) && NeedsReauthentication(message))
                return Failure<ProviderLyricsResult>(ProviderErrorKind.AccountNeedsReauthentication);
        }
        JsonElement matcherBody = default;
        if (calls.TryGetProperty("matcher.track.get", out var matcherCall))
        {
            if (TryGetCallBody(matcherCall, out var returnedBody, out var matcherError))
                matcherBody = returnedBody;
            else if (matcherError != null)
                return Failure<ProviderLyricsResult>(matcherError);
            else return Unavailable();
        }
        if (!TryReadTrack(matcherBody, out var metadata))
            return Unavailable();
        if (!Matches(request, metadata, diagnostics))
        {
            diagnostics.Decision = "identity-rejected";
            diagnostics.RejectedTrack = matcherBody.Clone();
            return Unavailable();
        }

        if (HasRestrictedContent(calls))
        {
            diagnostics.Decision = "restricted";
            return ProviderOutcome<ProviderLyricsResult>.Success(new(
                ProviderLyricsAvailabilityState.Restricted, StableProviderId));
        }

        ProviderError? deferredError = null;
        var timed = ReadRichsync(calls, "track.richsync.get", out var timedError);
        deferredError ??= timedError;
        var subtitle = ReadSubtitle(calls, "track.subtitles.get", out var subtitleError);
        deferredError ??= subtitleError;
        var plain = ReadPlain(calls, "track.lyrics.get", out var plainError);
        deferredError ??= plainError;

        diagnostics.RichsyncLines = CountLines(timed?.Content);
        diagnostics.SubtitleLines = CountLines(subtitle);
        diagnostics.PlainLines = CountLines(plain);
        var selected = SelectContent(request.PreferredFormat, timed, subtitle, plain);
        if (selected != null)
            diagnostics.Decision = $"selected:{selected.Value.Format}";
        return selected != null
            ? Available(selected.Value.Format, selected.Value.Content, selected.Value.TimedLines)
            : deferredError == null
                ? Unavailable()
                : Failure<ProviderLyricsResult>(deferredError);
    }

    private static int CountLines(string? content) => string.IsNullOrWhiteSpace(content)
        ? 0 : content.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));

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
                var start = value.AsSpan().TrimStart();
                if (start.StartsWith("[", StringComparison.Ordinal) &&
                    start[1..].TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    var rendered = ReadMxmSubtitle(value);
                    if (rendered != null) return rendered;
                }
                else
                {
                    var lines = MusicLyrics.ParseSyncedLines(value);
                    if (lines.Count > 0 && lines.All(line => line.StartMilliseconds <= MaximumTimestampMilliseconds))
                        return value;
                }
                error = ProviderError.CompatibilityContractChanged();
            }
        }
        return null;
    }

    private static string? ReadMxmSubtitle(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var rendered = new List<string>();
            foreach (var line in document.RootElement.EnumerateArray())
            {
                if (!TryGetString(line, "text", out var text) ||
                    text == null || text.Contains('\r') || text.Contains('\n') ||
                    !TryGetObject(line, "time", out var time) ||
                    !time.TryGetProperty("total", out var total) ||
                    !TryReadSeconds(total, out var seconds))
                    return null;
                rendered.Add(FormatTimestamp(seconds, text));
            }
            return rendered.Count > 0 ? string.Join('\n', rendered) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool NeedsReauthentication(JsonElement message)
    {
        if (TryGetStatus(message, out var status) && status is 401 or 402)
            return true;
        if (!TryGetObject(message, "header", out var header))
            return false;
        var hint = ReadString(header, "hint")?.Trim();
        return string.Equals(hint, "renew", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "captcha", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRestrictedContent(JsonElement calls)
    {
        foreach (var call in calls.EnumerateObject())
        {
            if (!TryGetObject(call.Value, "message", out var message) ||
                !TryGetObject(message, "body", out var body))
                continue;
            if (IsRestricted(body)) return true;
            if (TryGetObject(body, "lyrics", out var lyrics) &&
                lyrics.TryGetProperty("lyrics_body_percentage", out var percentage) &&
                percentage.ValueKind == JsonValueKind.Number &&
                percentage.TryGetDecimal(out var value) && value > 0 && value < 100)
                return true;
        }
        return false;
    }

    private static bool IsRestricted(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(IsRestricted);
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is "restricted" or "lyrics_dmca" or "subtitle_dmca" or "richsync_dmca" &&
                (property.Value.ValueKind == JsonValueKind.True ||
                 property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var flag) && flag != 0))
                return true;
            if (IsRestricted(property.Value)) return true;
        }
        return false;
    }

    private sealed class LookupDiagnostics
    {
        public JsonElement? RejectedTrack { get; set; }
        public string Envelope { get; private set; } = "missing/none";
        public string Matcher { get; private set; } = "missing/none";
        public string Lyrics { get; private set; } = "missing/none";
        public string Subtitles { get; private set; } = "missing/none";
        public string Richsync { get; private set; } = "missing/none";

        private static readonly string[] TrackKeyAllowlist =
        [
            "track_id", "track_name", "artist_name", "album_name", "has_lyrics", "has_subtitles",
            "has_richsync", "instrumental", "restricted", "commontrack_id", "track_length"
        ];

        public string MatcherBody { get; private set; } = "empty";
        public bool TrackPresent { get; private set; }
        public string TrackKeys { get; private set; } = "none";
        public int TitleScore { get; set; }
        public int ArtistScore { get; set; }
        public bool? HasLyrics { get; private set; }
        public bool? HasSubtitles { get; private set; }
        public bool? Instrumental { get; private set; }
        public bool? Restricted { get; private set; }
        public string LyricsBody { get; private set; } = "empty";
        public bool LyricsBodyPresent { get; private set; }
        public string SubtitleBody { get; private set; } = "empty";
        public int SubtitleCount { get; private set; }
        public string SubtitleFirst { get; private set; } = "empty";
        public string RichsyncBody { get; private set; } = "empty";
        public bool RichsyncBodyPresent { get; private set; }
        public int PlainLines { get; set; }
        public int SubtitleLines { get; set; }
        public int RichsyncLines { get; set; }
        public string Decision { get; set; } = "not-parsed";

        public void Read(JsonElement root)
        {
            if (!TryGetObject(root, "message", out var envelope)) return;
            Envelope = Describe(envelope);
            if (!TryGetObject(envelope, "body", out var body) ||
                !TryGetObject(body, "macro_calls", out var calls)) return;
            Matcher = DescribeCall(calls, "matcher.track.get");
            Lyrics = DescribeCall(calls, "track.lyrics.get");
            Subtitles = DescribeCall(calls, "track.subtitles.get");
            Richsync = DescribeCall(calls, "track.richsync.get");
            var matcherBody = ReadBody(calls, "matcher.track.get");
            MatcherBody = BodyKind(matcherBody);
            TrackPresent = matcherBody.ValueKind == JsonValueKind.Object && matcherBody.TryGetProperty("track", out _);
            if (TryGetObject(matcherBody, "track", out var track))
            {
                var keys = TrackKeyAllowlist.Where(key => track.TryGetProperty(key, out _)).ToArray();
                TrackKeys = keys.Length == 0 ? "none" : string.Join(',', keys);
                HasLyrics = ReadFlag(track, "has_lyrics");
                HasSubtitles = ReadFlag(track, "has_subtitles");
                Instrumental = ReadFlag(track, "instrumental");
                Restricted = ReadFlag(track, "restricted");
            }
            var lyricsBody = ReadBody(calls, "track.lyrics.get");
            LyricsBody = BodyKind(lyricsBody);
            LyricsBodyPresent = TryGetObject(lyricsBody, "lyrics", out var lyrics) && lyrics.TryGetProperty("lyrics_body", out _);
            var subtitleBody = ReadBody(calls, "track.subtitles.get");
            SubtitleBody = BodyKind(subtitleBody);
            if (subtitleBody.ValueKind == JsonValueKind.Object &&
                subtitleBody.TryGetProperty("subtitle_list", out var subtitles) && subtitles.ValueKind == JsonValueKind.Array)
            {
                SubtitleCount = subtitles.GetArrayLength();
                if (SubtitleCount > 0 && TryGetObject(subtitles[0], "subtitle", out var subtitle))
                {
                    var raw = ReadString(subtitle, "subtitle_body")?.TrimStart();
                    SubtitleFirst = string.IsNullOrEmpty(raw) ? "empty" : raw[0] == '[' ? "[" : "text";
                }
            }
            var richsyncBody = ReadBody(calls, "track.richsync.get");
            RichsyncBody = BodyKind(richsyncBody);
            RichsyncBodyPresent = TryGetObject(richsyncBody, "richsync", out var richsync) && richsync.TryGetProperty("richsync_body", out _);
        }

        private static JsonElement ReadBody(JsonElement calls, string name) =>
            TryGetObject(calls, name, out var call) && TryGetObject(call, "message", out var message) &&
            message.TryGetProperty("body", out var body) ? body : default;

        private static string BodyKind(JsonElement body) => body.ValueKind switch
        {
            JsonValueKind.Object => body.EnumerateObject().Any() ? "object" : "empty",
            JsonValueKind.Array => "array",
            JsonValueKind.Undefined or JsonValueKind.Null => "empty",
            _ => "other"
        };

        private static bool? ReadFlag(JsonElement track, string name)
        {
            if (!track.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number != 0 : null;
        }

        private static string DescribeCall(JsonElement calls, string name) =>
            TryGetObject(calls, name, out var call) && TryGetObject(call, "message", out var message)
                ? Describe(message) : "missing/none";

        private static string Describe(JsonElement message)
        {
            var status = TryGetStatus(message, out var code)
                ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing";
            var hint = TryGetObject(message, "header", out var header) ? ReadString(header, "hint")?.Trim() : null;
            var safeHint = hint?.ToLowerInvariant() switch
            {
                null or "" => "none",
                "renew" => "renew",
                "captcha" => "captcha",
                _ => "other"
            };
            return $"{status}/{safeHint}";
        }
    }

    private static string? ReadPlain(
        JsonElement calls,
        string name,
        out ProviderError? error)
    {
        error = null;
        if (!TryGetCallBody(calls, name, out var body, out error))
            return null;
        if (!TryGetObject(body, "lyrics", out var lyrics) ||
            !TryGetString(lyrics, "lyrics_body", out var value))
            return null;
        const string disclaimer = "******* This Lyrics is NOT for Commercial use *******";
        var footer = value!.IndexOf(disclaimer, StringComparison.OrdinalIgnoreCase);
        return (footer >= 0 ? value[..footer] : value).Trim();
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
        TrackMetadata actual,
        LookupDiagnostics diagnostics)
    {
        var title = NormalizeMatcherTitle(request.TrackTitle ?? string.Empty);
        var actualTitle = NormalizeMatcherTitle(actual.Title);
        diagnostics.TitleScore = FuzzyMatcher.CalculateSimilarity(title, actualTitle);
        diagnostics.ArtistScore = request.ArtistNames
            .Select(artist => ArtistSimilarity(artist, actual.Artist)).DefaultIfEmpty(0).Max();
        if (string.IsNullOrWhiteSpace(actual.Artist) || string.IsNullOrWhiteSpace(actualTitle))
            return false;
        if (!string.IsNullOrWhiteSpace(request.TrackTitle) &&
            (diagnostics.TitleScore < 90 ||
             Math.Min(title.Length, actualTitle.Length) < Math.Max(title.Length, actualTitle.Length) * 0.9))
            return false;
        return request.ArtistNames.Count == 0 || ArtistSimilarity(request.ArtistNames[0], actual.Artist) >= 90;
    }

    private static string NormalizeMatcherTitle(string title)
    {
        var stripped = FuzzyMatcher.StripDecorators(title);
        stripped = Regex.Replace(stripped, @"[\(\[]([^\)\]]+)[\)\]]",
            match => FuzzyMatcher.SemanticVersionTags(match.Groups[1].Value).Count > 0 ? " " : match.Value);
        stripped = Regex.Replace(stripped, @"\s+[-–—]\s+(.+)$",
            match => FuzzyMatcher.SemanticVersionTags(match.Groups[1].Value).Count > 0 ? " " : match.Value);
        return FuzzyMatcher.NormalizeForMatching(stripped);
    }

    private static int ArtistSimilarity(string requested, string actual)
    {
        var primary = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(requested).FirstOrDefault() ?? string.Empty);
        if (primary.Length == 0) return 0;
        var tokens = primary.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var actualTokens = FuzzyMatcher.NormalizeForMatching(actual).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var coverage = tokens.Count == 0 ? 0 : 100 * tokens.Count(actualTokens.Contains) / tokens.Count;
        var bestCredit = FuzzyMatcher.SplitCredits(actual)
            .Select(credit => FuzzyMatcher.CalculateSimilarity(primary, credit)).DefaultIfEmpty(0).Max();
        return Math.Max(coverage, bestCredit);
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
        ProviderAccountContext? account,
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
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                MusicBrainzService.NormalizeIsrc(request.Isrc) ?? string.Empty
            ]);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(metadata))).ToLowerInvariant();
        return $"lyrics:v4:{StableProviderId}:{account?.AccountId.ToString("N") ?? "automatic"}:{account?.Revision ?? 0}:" +
            $"{account?.SecretReferenceId?.ToString("N") ?? "none"}:{fingerprint}:" +
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
        if (status is 401 or 402)
            return new(ProviderErrorKind.AccountNeedsReauthentication);
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
