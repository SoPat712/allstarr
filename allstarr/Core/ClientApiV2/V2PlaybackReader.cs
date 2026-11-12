using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.ClientApiV2;

public sealed class V2PlaybackReader(V2TrackReader tracks, ProviderStreamOpener streams, V2PlaybackTokens tokens,
    JellyfinProxyService jellyfin, IEffectiveProviderPolicyResolver policies, IUpcomingTrackPreparation preparation,
    BackendIdentityResolver identities, IdentityOptions identityOptions, ProtocolStreamingResponseAdapter responses,
    IProviderRegistry registry, TimeProvider clock, MediaConversionGate conversions)
{
    internal static bool ValidTier(string tier) => tier is "lossless" or "high" or "normal" or "low" or "original";

    public async Task<V2Result> ResolveAsync(HttpContext http, ProtocolExecutionContext execution, V2PlaybackRequest request)
    {
        if (!ValidTier(request.Tier)) return new(400, null);
        var (read, sources) = await tracks.ReadWithSourcesAsync(http, execution, [request.TrackId], false);
        if (read.Tracks.Count == 0) return new(404, null);
        return new(200, await ResolveTrackAsync(http, execution, read.Tracks[0], sources[read.Tracks[0].Id], request));
    }

    internal async Task<V2PlaybackResponse> ResolveTrackAsync(HttpContext http, ProtocolExecutionContext execution,
        V2Track track, string item, V2PlaybackRequest request)
    {
        var household = await policies.ResolveAsync(execution.CancellationToken);
        var policy = execution.Policy ?? await policies.ResolveForUserAsync(execution.Principal!.UserId, execution.CancellationToken);
        var step = AudioQualityPolicy.Lower(household.AudioQuality, policy.Preferences.QualityLimit);
        var tier = step == "DataSaver" && request.Tier != "low" ? "low"
            : step == "High" && request.Tier is "lossless" or "original" ? "high" : request.Tier;
        var quality = tier is "lossless" or "original" ? ProviderAudioQuality.Any : ProviderAudioQuality.Lossy;
        quality = AudioQualityPolicy.Limit(quality, step);
        var parsed = LocalLibraryService.ParseExternalResource(item);
        var routed = parsed.isExternal
            ? await streams.InspectSourceAsync(execution, parsed.provider!, parsed.externalId!, quality) : null;
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
        var ticket = new V2PlaybackTicket(execution.Principal!.UserId, execution.BackendInstanceId,
            execution.VerifiedBackendPrincipalId, item, tier, playback, quality, Freeze(delivery, delivered), source);
        var lowered = tier != request.Tier || LimitChanged(source, step, request.Tier)
            ? step != household.AudioQuality ? "listener" : "household" : null;
        var reason = routed?.SelectionReason ?? "library";
        if (request.Tier == "lossless" && !Lossless(source.Codec)) reason += "; no lossless source available; using best available";
        if (tier == "lossless" && Lossless(source.Codec) && !Lossless(delivered.Codec)) reason += "; client codecs require lossy audio";
        if (lowered != null) reason += $"; quality limited by {lowered}";
        if (request.Purpose == "prefetch" && parsed.isExternal)
            await preparation.PrepareAsync(execution with { PreparationQuality = quality }, item);
        return new($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/allstarr/v2/stream/" + tokens.Protect(ticket, expires), expires, playback, key, Format(source), Format(delivered),
            converted, converted ? null : inspected.Content.Headers.ContentLength,
            !converted && (routed?.Lease.SupportsByteRanges == true || inspected.Headers.AcceptRanges.Contains("bytes")) ? "bytes" : "none",
            track.TrackGainDb, track.AlbumGainDb, new(provider, (registry.TryGet(provider, out var descriptor) ? descriptor!.DisplayName : null) ?? track.Provider?.Name ?? provider), reason, lowered);
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
            new(deviceId: ticket.PlaybackId))
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
        var conversion = ProviderAudioTranscoder.Plan(ticket.Delivery, ticket.Source);
        if (conversion != null)
        {
            http.Request.Headers.Remove("Range");
            http.Request.Headers.Remove("If-Range");
        }
        var parsed = LocalLibraryService.ParseExternalResource(ticket.Item);
        if (parsed.isExternal)
            return await streams.OpenAsync(http, execution, parsed.provider!, parsed.externalId!, ticket.Quality, ticket.Delivery,
                preserveSourceBytes: conversion == null);
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
            return await responses.CreateAsync(http, response, http.RequestAborted, enableRangeProcessing: false);
        }
        catch { response.Dispose(); throw; }
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
            MaxBitrate = target switch { "high" => 256000, "normal" => 160000, "low" => 96000, _ => null },
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
    private static V2PlaybackFormat Format(ProviderMediaFormat media) => new(media.Container, media.Codec, media.Bitrate, media.SampleRate, media.BitDepth, Lossless(media.Codec));
    private static ProviderMediaFormat Media(V2Format? format) => format?.Codec is not { } codec || format.Container is not { } container
        ? throw new ProviderAudioProfileException("Library audio format is unavailable.")
        : new("application/octet-stream", container, codec, format.Bitrate, format.SampleRate, format.BitDepth, format.Channels);
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
