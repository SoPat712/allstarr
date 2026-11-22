using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.ClientApiV2;

public sealed class V2PlaybackReader(V2TrackReader tracks, ProviderStreamOpener streams, V2PlaybackTokens tokens,
    JellyfinProxyService jellyfin, IEffectiveProviderPolicyResolver policies, IUpcomingTrackPreparation preparation,
    BackendIdentityResolver identities, IdentityOptions identityOptions, ProtocolStreamingResponseAdapter responses,
    IProviderRegistry registry, TimeProvider clock, MediaConversionGate conversions, V2ServingStreams serving, PlaybackReader current,
    V2ConvertedDownloads? downloads = null, ILogger<V2PlaybackReader>? logger = null)
{
    private const string ServedFromCopy = "allstarr.v2.stream.copy";

    internal static bool ValidTier(string tier) => tier is "lossless" or "high" or "normal" or "low" or "lowest" or "original";

    public async Task<V2Result> ResolveAsync(HttpContext http, ProtocolExecutionContext execution, V2PlaybackRequest request)
    {
        if (!ValidTier(request.Tier) || !V2Ids.TryDecode(request.TrackId, V2IdKind.Track, out _)) return new(400, null);
        var (read, sources) = await tracks.ReadWithSourcesAsync(http, execution, [request.TrackId], false);
        if (read.Tracks.Count == 0) return new(404, null);
        return new(200, await ResolveTrackAsync(http, execution, read.Tracks[0], sources[read.Tracks[0].Id], request));
    }

    internal async Task<V2PlaybackResponse> ResolveTrackAsync(HttpContext http, ProtocolExecutionContext execution,
        V2Track track, string item, V2PlaybackRequest request)
    {
        var household = await policies.ResolveAsync(execution.CancellationToken);
        var policy = execution.Policy ?? await policies.ResolveForUserAsync(execution.Principal!.UserId, execution.CancellationToken);
        var parsed = LocalLibraryService.ParseExternalResource(item);
        // The household's limit caps what services send. A file in the listener's own library plays as it is, unless the
        // listener set a lower limit of their own or the app asks for less.
        var step = parsed.isExternal ? AudioQualityPolicy.Lower(household.AudioQuality, policy.Preferences.QualityLimit)
            : AudioQualityPolicy.Steps.Contains(policy.Preferences.QualityLimit ?? "") ? policy.Preferences.QualityLimit! : AudioQualityPolicy.DefaultStep;
        var tier = step == "DataSaver" && request.Tier is not ("low" or "lowest") ? "low"
            : step == "High" && request.Tier is "lossless" or "original" ? "high" : request.Tier;
        var quality = tier is "lossless" or "original" ? ProviderAudioQuality.Any : ProviderAudioQuality.Lossy;
        quality = AudioQualityPolicy.Limit(quality, step);
        var routed = parsed.isExternal
            ? await streams.InspectSourceAsync(execution, parsed.provider!, parsed.externalId!, quality) : null;
        // Some services only know a song's format once they have fetched it. A download is promised a format and byte
        // ranges, so its song is fetched first and the copy the server then holds is asked what it is; anything else
        // starts at once and is judged when it opens.
        if (routed is { Response.IsSuccessStatusCode: true } && request.Purpose == "download" && Unknown(routed.FileFacts.Media ?? routed.Lease.Media))
        {
            routed.Response.Dispose();
            await preparation.PrepareAsync(execution with { PreparationQuality = quality }, item);
            routed = await streams.InspectSourceAsync(execution, parsed.provider!, parsed.externalId!, quality, held: true);
        }
        using var inspected = parsed.isExternal
            ? routed?.Response
            : await jellyfin.OpenAudioSourceAsync(item, true, null, execution.CancellationToken);
        if (inspected == null || !inspected.IsSuccessStatusCode) throw new HttpRequestException("No playback source is available.");
        var source = routed?.FileFacts.Media ?? routed?.Lease.Media ?? Media(track.Format);
        if (tier == "original" && (step == "CdLossless" && (source.SampleRate > 44100 || source.BitDepth > 16) ||
            step == "HiResLossless" && source.SampleRate > 96000)) tier = "lossless";
        var delivery = Delivery(tier, request.Client, source, step);
        var decision = delivery.Decide(source, inspected.Content.Headers.ContentLength);
        if (!decision.Supported || decision.Media == null) throw new ProviderAudioProfileException("No supported playback format is available.");
        var delivered = decision.Media;
        var converted = decision.SupportsTranscoding;
        var provider = routed?.ServingProviderId ?? track.Provider?.Id ?? "jellyfin";
        var sourceKey = SourceKey(item, track.SourceVersion, inspected, source, routed);
        var key = Hash(JsonSerializer.Serialize(new { track.Id, tier = request.Tier, delivered = Format(delivered), delivered.Channels, sourceKey }));
        var playback = Guid.NewGuid().ToString("N");
        var expires = clock.GetUtcNow().AddHours(12);
        // A download of a converted tier is made once into a file and served from it, so it has a length and can resume.
        // A service song's copy is named by the song at the service, not by where its source sits: the first download
        // moves the source into the server's cache, and a link resolved after that must still find the same copy.
        var copy = !converted || request.Purpose != "download" || downloads == null ? null
            : routed == null ? key
            : Hash(JsonSerializer.Serialize(new
            {
                track.Id,
                tier = request.Tier,
                delivered = Format(delivered),
                delivered.Channels,
                provider = routed.ServingProviderId,
                externalId = routed.ServingExternalId ?? parsed.externalId
            }));
        var held = copy == null ? null : downloads!.Find(copy);
        var ticket = new V2PlaybackTicket(execution.Principal!.UserId, execution.BackendInstanceId,
            execution.VerifiedBackendPrincipalId, item, tier, playback, quality, Pinned(parsed.isExternal, tier, source) ? Freeze(delivery, delivered) : delivery, source)
        { DeviceId = execution.Client.DeviceId, TrackId = V2AutoplayReader.Identity(track), Copy = copy, Purpose = request.Purpose };
        var lowered = tier != request.Tier || LimitChanged(source, step, request.Tier)
            ? !parsed.isExternal || step != household.AudioQuality ? "listener" : "household" : null;
        var reason = routed?.SelectionReason ?? "library";
        if (request.Tier == "lossless" && !Lossless(source.Codec)) reason += "; no lossless source available; using best available";
        if (tier == "lossless" && Lossless(source.Codec) && !Lossless(delivered.Codec)) reason += "; client codecs require lossy audio";
        if (lowered != null) reason += $"; quality limited by {lowered}";
        if (request.Purpose == "prefetch" && parsed.isExternal)
            await preparation.PrepareAsync(execution with { PreparationQuality = quality }, item);
        serving.Remember(execution, playback, expires);
        return new($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/allstarr/v2/stream/" + tokens.Protect(ticket, expires), expires, playback, key, Format(source), Format(delivered),
            converted, converted ? held?.Length : inspected.Content.Headers.ContentLength is > 0 and var length ? length : null,
            copy != null || !converted && (routed?.IsCached == true || routed?.Lease.SupportsByteRanges == true || inspected.Headers.AcceptRanges.Contains("bytes")) ? "bytes" : "none",
            track.TrackGainDb, track.AlbumGainDb, new(provider, (registry.TryGet(provider, out var descriptor) ? descriptor!.DisplayName : null) ?? track.Provider?.Name ?? provider), reason, lowered)
        { Serving = await ServingAsync(http, execution, track, item) };
    }

    private async Task<V2ServingFacts?> ServingAsync(HttpContext http, ProtocolExecutionContext execution, V2Track track, string source)
    {
        if ((serving.Read(execution, V2AutoplayReader.Identity(track)) ?? serving.Read(execution, source)) is { } active) return active;
        var playing = await current.CurrentAsync(http, execution);
        if (playing?.Delivered is not { } media || playing.Provider?.Id is not { } provider ||
            !track.Aliases.Append(track.Id).Append(source).Contains(playing.ItemId, StringComparer.Ordinal)) return null;
        return new(media.Codec, media.Bitrate, media.BitDepth, media.SampleRate, provider);
    }

    public async Task<IActionResult> OpenAsync(HttpContext http, string token)
    {
        var ticket = tokens.Read(token);
        if (ticket == null || ticket.Backend != identityOptions.BackendInstanceId) return new NotFoundResult();
        if (AuthHeaderHelper.HasAuthentication(http.Request.Headers))
        {
            var (body, status) = await jellyfin.GetJsonAsync("Users/Me", clientHeaders: http.Request.Headers, cancellationToken: http.RequestAborted);
            using (body)
                if (status != 200 || body == null || !body.RootElement.TryGetProperty("Id", out var id) || id.GetString() != ticket.Principal)
                    return new NotFoundResult();
        }
        AllstarrPrincipal? principal;
        try { principal = await identities.ResolveAsync(new("jellyfin", ticket.Principal, BackendInstanceId: ticket.Backend), http.RequestAborted); }
        catch (UnauthorizedAccessException) { return new NotFoundResult(); }
        if (principal == null || principal.UserId != ticket.Listener) return new NotFoundResult();
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, ticket.Backend, ticket.Principal,
            principal with { IsAdministrator = false }, ticket.PlaybackId, clock.GetUtcNow().AddMinutes(3), http.RequestAborted,
            new(deviceId: ticket.DeviceId))
        {
            RequestedItemId = ticket.Item,
            UseStoredBackendCredentials = true,
            Policy = await policies.ResolveForUserAsync(ticket.Listener, http.RequestAborted)
        };
        http.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = execution.Principal;
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = execution;
        PlaybackStartTimings.Current?.Correlate(Guid.Parse(ticket.PlaybackId));
        http.Response.Headers["X-Allstarr-Playback-Request"] = ticket.PlaybackId;
        http.Response.OnStarting(() => { http.Response.Headers["X-Allstarr-Playback-Request"] = ticket.PlaybackId; return Task.CompletedTask; });
        Note(http, ticket);
        var conversion = ProviderAudioTranscoder.Plan(ticket.Delivery, ticket.Source);
        if (conversion != null && ticket.Copy != null && downloads != null && await OpenCopyAsync(http, ticket, conversion) is { } file) return file;
        if (conversion != null)
        {
            http.Request.Headers.Remove("Range");
            http.Request.Headers.Remove("If-Range");
        }
        var parsed = LocalLibraryService.ParseExternalResource(ticket.Item);
        if (parsed.isExternal)
            return await streams.OpenAsync(http, execution, parsed.provider!, parsed.externalId!, ticket.Quality, ticket.Delivery,
                preserveSourceBytes: conversion == null,
                serving: (media, provider) => serving.Observe(http, execution, ticket.TrackId ?? ticket.Item, media, provider));
        var response = await jellyfin.OpenAudioSourceAsync(ticket.Item, HttpMethods.IsHead(http.Request.Method),
            conversion == null ? http.Request.Headers : null, http.RequestAborted);
        if (!response.IsSuccessStatusCode)
        { var status = (int)response.StatusCode; response.Dispose(); return new StatusCodeResult(status); }
        try
        {
            if (conversion != null)
            {
                if (HttpMethods.IsHead(http.Request.Method)) ProviderAudioTranscoder.ApplyHead(response, http.Response, conversion);
                else await ProviderAudioTranscoder.ApplyAsync(response, http.Response, conversion, conversions, http.RequestAborted);
            }
            var result = await responses.CreateAsync(http, response, http.RequestAborted, enableRangeProcessing: false);
            serving.Observe(http, execution, ticket.TrackId ?? ticket.Item, conversion?.Media ?? ticket.Source, "jellyfin");
            return result;
        }
        catch { response.Dispose(); throw; }
    }

    /// <summary>
    /// Notes each stream request once it has ended: what was asked for and how much of it went out. A request the
    /// listener's app cut off leaves no other trace, and on a poor connection the question is whether the next request
    /// picked up where this one stopped or started over.
    /// </summary>
    private void Note(HttpContext http, V2PlaybackTicket ticket)
    {
        if (logger == null) return;
        var rangeAsked = http.Request.Headers.ContainsKey("Range");
        var started = clock.GetTimestamp();
        var body = new CountedResponseBody(http.Response.Body);
        http.Response.Body = body;
        http.Response.OnCompleted(() =>
        {
            logger.LogInformation(
                "Stream request ended: {Method} of a {Kind} song at tier {Tier} for {Purpose}, {Delivery}; answered {StatusCode}, sent {SentBytes} of {LengthBytes} bytes, range asked {RangeAsked}, client left {ClientLeft}, {ElapsedMs} ms",
                http.Request.Method, LocalLibraryService.ParseExternalResource(ticket.Item).isExternal ? "service" : "library", ticket.Tier,
                ticket.Purpose ?? "play", http.Items.ContainsKey(ServedFromCopy) ? "from a kept copy" : "as it is read",
                http.Response.StatusCode, body.Sent, http.Response.ContentLength, rangeAsked, http.RequestAborted.IsCancellationRequested,
                (long)clock.GetElapsedTime(started).TotalMilliseconds);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// A download's converted copy as a file with a length and byte ranges, made first when it is not held yet: the
    /// conversion is read from this server's own stream route, so it is the stream a listener would have been sent.
    /// Null for a HEAD before the copy exists, which is answered as any conversion is.
    /// </summary>
    private async Task<IActionResult?> OpenCopyAsync(HttpContext http, V2PlaybackTicket ticket, ProviderAudioConversion conversion)
    {
        var copy = downloads!.Find(ticket.Copy!);
        if (copy == null)
        {
            if (HttpMethods.IsHead(http.Request.Method)) return null;
            var inner = tokens.Protect(ticket with { Copy = null }, clock.GetUtcNow().AddMinutes(10));
            var made = await downloads.MakeAsync(ticket.Copy!, new($"http://127.0.0.1:{http.Connection.LocalPort}{http.Request.PathBase}/allstarr/v2/stream/{inner}"),
                http.RequestAborted);
            if (made.File == null)
            {
                // Both conversion slots taken, or the source would not open: the app asks again rather than failing the song.
                if (made.RetryAfter is { } wait)
                    http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new StatusCodeResult(made.Status >= 400 ? made.Status : StatusCodes.Status503ServiceUnavailable);
            }
            copy = made.File;
        }
        http.Items[ServedFromCopy] = true;
        http.Response.Headers["X-Allstarr-Audio-Codec"] = conversion.Media.Codec;
        http.Response.Headers["X-Allstarr-Audio-Container"] = conversion.Media.Container;
        if (conversion.Media.Bitrate is { } bitrate)
            http.Response.Headers["X-Allstarr-Audio-Bitrate"] = bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new PhysicalFileResult(copy.FullName, conversion.Media.MimeType)
        {
            EnableRangeProcessing = true,
            LastModified = copy.LastWriteTimeUtc,
            EntityTag = Microsoft.Net.Http.Headers.EntityTagHeaderValue.Parse(V2ConvertedDownloads.EntityTag(ticket.Copy!, copy))
        };
    }

    internal static ProviderAudioDeliveryRequest Delivery(string tier, V2PlaybackClient? client, ProviderMediaFormat source, string step)
    {
        var codec = client != null && !client.Codecs.Contains("aac", StringComparer.OrdinalIgnoreCase) ? "mp3" : "aac";
        var lossless = client?.Codecs.Where(codec => string.Equals(codec, "flac", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(codec, "alac", StringComparison.OrdinalIgnoreCase)).ToArray() ?? ["flac", "alac"];
        var keep = tier == "original" || tier == "lossless" && !Lossless(source.Codec) &&
            (client == null || client.Codecs.Contains(source.Codec, StringComparer.OrdinalIgnoreCase));
        var target = tier == "lossless" && !keep && (!Lossless(source.Codec) || lossless.Length == 0) ? "high" : tier;
        return new()
        {
            Formats = keep ? null : [new([], target == "lossless" ? lossless : [codec], ["http"])],
            PreferredCodec = target == "lossless" ? "flac" : codec,
            EnableTranscoding = tier != "original",
            MaxBitrate = target switch { "high" => 256000, "normal" => 160000, "low" => 96000, "lowest" => 64000, _ => null },
            MaxSampleRate = tier == "original" ? null : Minimum(client?.MaxSampleRate, step switch { "CdLossless" => 44100, "HiResLossless" => 96000, _ => null }),
            MaxBitDepth = tier == "original" ? null : Minimum(client?.MaxBitDepth, step == "CdLossless" ? 16 : null),
            MaxChannels = tier == "original" ? null : client?.Channels
        };
    }

    private static int? Minimum(int? first, int? second) => first == null ? second : second == null ? first : Math.Min(first.Value, second.Value);
    private static bool LimitChanged(ProviderMediaFormat source, string step, string tier) =>
        tier is "lossless" or "original" && (step == "CdLossless" && (source.SampleRate > 44100 || source.BitDepth > 16) ||
            step == "HiResLossless" && source.SampleRate > 96000);
    private static bool Lossless(string codec) => codec is "flac" or "alac" or "wav" or "aiff" or "ape" or "wavpack" || codec.StartsWith("pcm_", StringComparison.Ordinal);
    internal static V2PlaybackFormat Format(ProviderMediaFormat media) => new(media.Container, media.Codec, media.Bitrate, media.SampleRate, media.BitDepth, Lossless(media.Codec));
    private static ProviderMediaFormat Media(V2Format? format) => format?.Codec is not { } codec || format.Container is not { } container
        ? throw new ProviderAudioProfileException("Library audio format is unavailable.")
        : new("application/octet-stream", container, codec, format.Bitrate, format.SampleRate, format.BitDepth, format.Channels);
    private static bool Unknown(ProviderMediaFormat media) => media.Codec == "unknown" || media.Container == "unknown";
    // A link is pinned to the format resolve promised, so that a source that changed is converted back to it. That
    // cannot be done for a format not known yet, and must not be done for a service song asked for as it is: what a
    // service says about a song before sending it is approximate, nothing may be converted, and so any difference
    // would refuse the song. Such a link carries the tier's own limits and is judged when it opens.
    private static bool Pinned(bool service, string tier, ProviderMediaFormat source) => !Unknown(source) && !(service && tier == "original");
    private static ProviderAudioDeliveryRequest Freeze(ProviderAudioDeliveryRequest request, ProviderMediaFormat media) => request with
    {
        Formats = [new([media.Container], [media.Codec], ["http"])],
        SampleRate = media.SampleRate,
        Channels = media.Channels,
        MaxBitrate = Lossless(media.Codec) ? null : media.Bitrate,
        MaxBitDepth = media.BitDepth,
        Conditions = new[] { ("AudioBitrate", Lossless(media.Codec) ? null : media.Bitrate), ("AudioSampleRate", media.SampleRate),
            ("AudioBitDepth", media.BitDepth), ("AudioChannels", media.Channels) }
            .Where(fact => fact.Item2.HasValue).Select(fact => new ProviderAudioCondition("", fact.Item1, "Equals",
                [fact.Item2!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)], true)).ToArray()
    };
    private static string SourceKey(string item, string? version, HttpResponseMessage response, ProviderMediaFormat media, ProtocolProviderStream? routed)
    {
        if (routed?.StoredFilePath is { } path)
        {
            var file = new FileInfo(path);
            return Hash(JsonSerializer.Serialize(new { path, file.Length, file.LastWriteTimeUtc }));
        }
        return routed == null
            ? Hash(JsonSerializer.Serialize(new { item, etag = response.Headers.ETag?.ToString(), modified = response.Content.Headers.LastModified, version }))
            : Hash(JsonSerializer.Serialize(new
            {
                provider = routed.ServingProviderId,
                externalId = routed.ServingExternalId ?? LocalLibraryService.ParseExternalResource(item).externalId,
                source = Format(media),
                media.Channels
            }));
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
