using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Core.Providers.Monochrome;

public sealed class MonochromeStreamingCapabilityAdapter(
    HttpClient http,
    ILogger<MonochromeStreamingCapabilityAdapter>? logger = null) : IProviderRecordingStreamingCapability, IProviderDiagnosticSampleSearch
{
    private const string Host = "tracks.monochrome.st";
    private const int MaximumSearchBytes = 256 * 1024;
    private const long MaximumAudioBytes = 2L * 1024 * 1024 * 1024;
    private static readonly ProviderMediaFormat Media = new("audio/flac", "flac", "flac");
    private readonly ILogger<MonochromeStreamingCapabilityAdapter> _logger = logger ?? NullLogger<MonochromeStreamingCapabilityAdapter>.Instance;

    public string ProviderId => "monochrome";
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;
    public bool SupportsRecordingAvailability => true;

    public async Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
        ProviderExecutionContext context, ProviderStreamLeaseRequest request)
    {
        var error = Validate(context, request.TrackId);
        if (error != null) return ProviderOutcome<ProviderStreamLease>.Failure(error);
        if (!IsNativeId(request.TrackId.Value)) return Failure<ProviderStreamLease>(ProviderErrorKind.NotFound);
        if (!AllowsLossless(context, request.RequestedQuality)) return Failure<ProviderStreamLease>(ProviderErrorKind.IncompatibleMedia);
        try
        {
            var uri = new Uri($"https://{Host}/track/{request.TrackId.Value}");
            using var probe = await SendAsync(uri, HttpMethod.Get, new RangeHeaderValue(0, 0), context.CancellationToken);
            error = ErrorFor(probe);
            if (error != null) return ProviderOutcome<ProviderStreamLease>.Failure(error);
            if (!IsAudio(probe) || TooLarge(probe)) return Failure<ProviderStreamLease>(ProviderErrorKind.IncompatibleMedia);
            var ranges = probe.StatusCode == HttpStatusCode.PartialContent &&
                probe.Content.Headers.ContentRange?.Length is > 0 &&
                StreamByteRanges.Matches(probe, new RangeHeaderValue(0, 0));
            if (probe.StatusCode == HttpStatusCode.PartialContent && !ranges)
                return Failure<ProviderStreamLease>(ProviderErrorKind.IncompatibleMedia);
            var media = Media;
            if (probe.Content.Headers.ContentType?.MediaType is "audio/mp4" or "application/mp4" ||
                probe.Content.Headers.ContentType?.MediaType == "application/octet-stream" &&
                await FirstByteAsync(probe.Content, context.CancellationToken) != (byte)'f')
            {
                media = await MonochromeAudioFormat.ReadAsync((range, token) => SendAsync(uri, HttpMethod.Get, range, token),
                    context.CancellationToken);
                if (media == null || media.Codec == "aac" && context.Policy.Quality.Minimum > ProviderAudioQuality.Lossy)
                    return Failure<ProviderStreamLease>(ProviderErrorKind.IncompatibleMedia);
            }
            var downgrade = media.Codec == "aac" ? "Monochrome returned lossy AAC for this recording." :
                request.RequestedQuality == ProviderAudioQuality.HighResolution
                    ? "Monochrome supplies lossless audio; high resolution is not guaranteed." : null;
            return ProviderOutcome<ProviderStreamLease>.Success(new(
                $"monochrome-{request.TrackId.Value}", uri, DateTimeOffset.UtcNow.AddMinutes(5), ranges, ranges, media,
                ProviderStreamRetryBehavior.DoNotRetry,
                (outbound, token) => OpenAsync(uri, ranges, outbound, token),
                downgrade));
        }
        catch (OperationCanceledException) { return Failure<ProviderStreamLease>(context.CancellationToken.IsCancellationRequested ? ProviderErrorKind.Canceled : ProviderErrorKind.TransientFailure); }
        catch (HttpRequestException) { return Failure<ProviderStreamLease>(ProviderErrorKind.TransientFailure); }
        catch (IOException) { return Failure<ProviderStreamLease>(ProviderErrorKind.TransientFailure); }
    }

    public async Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(
        ProviderExecutionContext context, ProviderStreamLeaseRequest request)
    {
        var lease = await GetStreamLeaseAsync(context, request);
        return lease.IsSuccess ? ProviderOutcome<ProviderStreamProbeResult>.Success(new(true, DateTimeOffset.UtcNow, lease.RequireValue().Media)) :
            ProviderOutcome<ProviderStreamProbeResult>.Failure(lease.Error!);
    }

    public Task<ProviderOutcome<ProviderRecordingAvailability>> ResolveRecordingAsync(
        ProviderExecutionContext context, ProviderRecordingAvailabilityRequest recording, ProviderAudioQuality quality) =>
        FindRecordingAsync(context, recording, quality, true);

    public Task<ProviderOutcome<ProviderRecordingAvailability>> DiscoverSampleAsync(
        ProviderExecutionContext context, string query, ProviderAudioQuality quality) =>
        FindRecordingAsync(context, new("discovery", query, ["Music"], null, null, new Dictionary<string, string>()), quality, false);

    private async Task<ProviderOutcome<ProviderRecordingAvailability>> FindRecordingAsync(
        ProviderExecutionContext context, ProviderRecordingAvailabilityRequest recording, ProviderAudioQuality quality, bool exact)
    {
        var error = Validate(context);
        if (error != null) return ProviderOutcome<ProviderRecordingAvailability>.Failure(error);
        if (!AllowsLossless(context, quality)) return Failure<ProviderRecordingAvailability>(ProviderErrorKind.IncompatibleMedia);
        var isrc = NormalizeIsrc(recording.Isrc);
        if (exact && isrc == null || recording.Title.Length is 0 or > 500 || recording.ArtistNames.Count == 0)
            return Unresolved();
        var query = exact ? $"{recording.ArtistNames[0]} {recording.Title}".Trim() : recording.Title;
        if (query.Length > 1000) return Unresolved();
        try
        {
            var uri = new Uri($"https://{Host}/search/tracks?q={Uri.EscapeDataString(query)}&limit=6");
            using var response = await SendAsync(uri, HttpMethod.Get, null, context.CancellationToken, json: true);
            error = ErrorFor(response);
            if (error != null) return ProviderOutcome<ProviderRecordingAvailability>.Failure(error);
            if (response.Content.Headers.ContentLength is > MaximumSearchBytes)
                return Failure<ProviderRecordingAvailability>(ProviderErrorKind.CapabilityUnavailable);
            using var body = new MemoryStream();
            await using var source = await response.Content.ReadAsStreamAsync(context.CancellationToken);
            var buffer = new byte[8192];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var read = await source.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                if (body.Length + read > MaximumSearchBytes) return Failure<ProviderRecordingAvailability>(ProviderErrorKind.CapabilityUnavailable);
                body.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() > 100)
                return Failure<ProviderRecordingAvailability>(ProviderErrorKind.CapabilityUnavailable);
            var matches = tracks.EnumerateArray().Where(track => track.ValueKind == JsonValueKind.Object &&
                (!exact || NormalizeIsrc(Text(track, "isrc")) == isrc) &&
                (!track.TryGetProperty("playable", out var playable) || playable.ValueKind != JsonValueKind.False))
                .Select(track => NativeId(track, "trackId") ?? NativeId(track, "id"))
                .Where(id => id != null && IsNativeId(id)).Distinct(StringComparer.Ordinal).ToArray();
            return (exact ? matches.Length == 1 : matches.Length > 0) ? ProviderOutcome<ProviderRecordingAvailability>.Success(new(
                new("monochrome", ProviderResourceKind.Track, matches[0]!))) : Unresolved();
        }
        catch (OperationCanceledException) { return Failure<ProviderRecordingAvailability>(context.CancellationToken.IsCancellationRequested ? ProviderErrorKind.Canceled : ProviderErrorKind.TransientFailure); }
        catch (HttpRequestException) { return Failure<ProviderRecordingAvailability>(ProviderErrorKind.TransientFailure); }
        catch (IOException) { return Failure<ProviderRecordingAvailability>(ProviderErrorKind.TransientFailure); }
        catch (JsonException) { return Failure<ProviderRecordingAvailability>(ProviderErrorKind.CapabilityUnavailable); }
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri source, bool ranges, HttpRequestMessage original, CancellationToken token)
    {
        if (original.RequestUri != source || original.Method != HttpMethod.Get && original.Method != HttpMethod.Head)
            throw new HttpRequestException("The provider stream request is invalid.");
        var started = Stopwatch.GetTimestamp();
        var status = 0;
        try
        {
            var response = await SendAsync(source, original.Method, ranges ? original.Headers.Range : null, token);
            if (original.Method == HttpMethod.Head && response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                response.Dispose();
                response = await SendAsync(source, HttpMethod.Get, null, token);
            }
            status = (int)response.StatusCode;
            response.Headers.Remove("X-Allstarr-Duration-Seconds");
            if (response.IsSuccessStatusCode && (!IsAudio(response) || TooLarge(response)))
            {
                response.Dispose();
                return new(HttpStatusCode.BadGateway);
            }
            if (response.IsSuccessStatusCode && original.Method != HttpMethod.Head)
            {
                try
                {
                    var owner = response.Content;
                    var limited = new StreamContent(new LimitedAudioStream(await owner.ReadAsStreamAsync(token), owner));
                    foreach (var header in owner.Headers) limited.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    response.Content = limited;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            return response;
        }
        finally
        {
            _logger.LogInformation("Monochrome stream open status={Status} rung={Rung} latencyMs={LatencyMs}",
                status, ranges ? "byte-range" : "progressive", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, RangeHeaderValue? range,
        CancellationToken token, bool json = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        for (var hop = 0; ; hop++)
        {
            if (!SafeUri(uri)) throw new HttpRequestException("The provider target is unavailable.");
            using var request = new HttpRequestMessage(method, uri);
            if (range != null && method == HttpMethod.Get) request.Headers.Range = range;
            if (json) request.Headers.Accept.Add(new("application/json"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (hop >= 3 || location == null) throw new HttpRequestException("The provider redirect is unavailable.");
            uri = new Uri(uri, location);
        }
    }

    private static async Task<int> FirstByteAsync(HttpContent content, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var stream = await content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[1];
        return await stream.ReadAsync(buffer, timeout.Token) == 0 ? -1 : buffer[0];
    }

    private static bool SafeUri(Uri uri) => OutboundRequestGuard.TryCreateSafeHttpUri(uri.AbsoluteUri, out var safe, out _) &&
        safe!.Scheme == Uri.UriSchemeHttps && safe.Port == 443 && safe.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(safe.Fragment);
    private static bool IsNativeId(string id) => id.Length is >= 17 and <= 20 && id.All(char.IsAsciiDigit);
    private static bool IsAudio(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType is "audio/flac" or "audio/x-flac" or "application/octet-stream" or "audio/mp4" or "application/mp4";
    private static bool TooLarge(HttpResponseMessage response) => response.Content.Headers.ContentLength is > MaximumAudioBytes || response.Content.Headers.ContentRange?.Length is > MaximumAudioBytes;
    private static string? NativeId(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetRawText() :
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? NormalizeIsrc(string? value)
    {
        var code = value?.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return code is { Length: 12 } && code[..2].All(char.IsAsciiLetter) && code[2..5].All(char.IsAsciiLetterOrDigit) && code[5..].All(char.IsAsciiDigit) ? code : null;
    }
    private static bool AllowsLossless(ProviderExecutionContext context, ProviderAudioQuality quality) =>
        quality is not (ProviderAudioQuality.DataSaver or ProviderAudioQuality.Lossy) &&
        context.Policy.Quality.Minimum <= ProviderAudioQuality.Lossless && context.Policy.Quality.Maximum >= ProviderAudioQuality.Lossless;

    private static ProviderError? Validate(ProviderExecutionContext context, ProviderExternalResourceId? id = null)
    {
        if (context.ProviderId != "monochrome" || !context.Policy.AllowsProvider("monochrome") || context.Account != null ||
            id != null && (id.ProviderId != "monochrome" || id.ResourceKind != ProviderResourceKind.Track)) return new(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested) return new(ProviderErrorKind.Canceled);
        return context.IsExpired(DateTimeOffset.UtcNow) ? new(ProviderErrorKind.CapabilityUnavailable) : null;
    }

    private static ProviderError? ErrorFor(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.NotFound or HttpStatusCode.Gone => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited,
            response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ?
                TimeSpan.FromSeconds(Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds)) : TimeSpan.FromSeconds(30))),
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ when !response.IsSuccessStatusCode => new(ProviderErrorKind.CapabilityUnavailable),
        _ => null
    };
    private static ProviderOutcome<ProviderRecordingAvailability> Unresolved() => ProviderOutcome<ProviderRecordingAvailability>.Success(new(null));
    private static ProviderOutcome<T> Failure<T>(ProviderErrorKind kind) => ProviderOutcome<T>.Failure(new(kind));

    private sealed class LimitedAudioStream(Stream source, HttpContent owner) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var read = await source.ReadAsync(buffer, timeout.Token);
            _read += read;
            if (_read > MaximumAudioBytes) throw new IOException("The provider audio exceeded the size limit.");
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) owner.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
