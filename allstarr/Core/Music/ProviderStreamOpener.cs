using allstarr.Core.Playback;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Services.Local;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Music;

/// <summary>
/// Opens a provider song for a protocol stream request: the routed source is opened once,
/// complete responses may be kept in the managed cache, and the response keeps the client's
/// range and HEAD semantics. Failures propagate so each protocol can describe them its own way.
/// </summary>
public sealed class ProviderStreamOpener(
    IProtocolProviderGateway providers,
    ProtocolStreamingResponseAdapter responses,
    ManagedTrackCacheService? cache = null,
    PlaybackDeliveryActivityStore? activity = null,
    ILogger<ProviderStreamOpener>? logger = null,
    ProviderStreamAdmission? admission = null,
    MediaConversionGate? conversions = null,
    PlaybackStartTimingStore? timings = null)
{
    internal static readonly IReadOnlySet<string> PreparationProviders = new HashSet<string>(StringComparer.Ordinal)
    { "apple-musickit", "apple-download" };

    public async Task PrepareAsync(ProtocolExecutionContext context, string itemId)
    {
        if (!context.IsPreparation || cache?.CanPrepare(context) != true) return;
        using var capacity = admission?.TryEnter(context);
        if (admission != null && capacity == null) return;
        var (external, provider, kind, id) = LocalLibraryService.ParseExternalResource(itemId);
        if (!external || kind != "song" || provider == null || id == null) return;
        var started = Stopwatch.GetTimestamp();
        var outcome = "skipped";
        long bytes = 0;
        var selected = PreparationProviders.Contains(provider) ? provider : "unknown";
        try
        {
            if (context.AudioDelivery?.RequestError is { } invalid) throw new ProviderAudioProfileException(invalid);
            var quality = context.AudioDelivery?.Quality is { } requested && requested != ProviderAudioQuality.Any
                ? requested : context.PreparationQuality;
            var routed = await providers.OpenStreamAsync(context, provider, id, quality, null);
            if (routed == null) return;
            using var response = routed.Response;
            selected = routed.ServingProviderId;
            if (context.AudioDelivery?.Decide(routed.FileFacts.Media ?? routed.Lease.Media) is { Supported: false } rejected)
                throw new ProviderAudioProfileException(rejected.Error!);
            if (routed.IsCached || !PreparationProviders.Contains(selected) || !response.IsSuccessStatusCode) return;
            var servingId = routed.ServingExternalId ?? id;
            Task<allstarr.Models.Domain.Song?>? metadata = null;
            Task<allstarr.Models.Domain.Song?> Metadata() => metadata ??= providers.GetSongAsync(context, selected, servingId);
            StreamDurationGuidance.Attach(routed, Metadata);
            await cache.WrapAsync(routed, context, selected, servingId, false, Metadata, context.CancellationToken);
            if (ManagedTrackCacheService.SeekRung(response.Content) == null) return;
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken,
                ManagedTrackCacheService.PlaybackStarted(response.Content));
            var stream = await response.Content.ReadAsStreamAsync(reading.Token);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, reading.Token)) != 0) bytes += read;
            outcome = "completed";
        }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        catch { outcome = context.CancellationToken.IsCancellationRequested ? "cancelled" : "failed"; }
        finally
        {
            logger?.LogInformation("Upcoming song preparation {Provider} {Outcome} in {ElapsedMilliseconds} ms ({Bytes} bytes)",
                selected, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds, bytes);
        }
    }

    public async Task<ProviderAudioDeliveryDecision> InspectAsync(
        ProtocolExecutionContext context,
        string providerId,
        string externalId,
        ProviderAudioDeliveryRequest request)
    {
        var routed = await providers.OpenStreamAsync(context with { IsReadOnly = true, AudioDelivery = null },
            providerId, externalId, request.Quality, null, true);
        if (routed == null) return request.Evaluate(null);
        using var response = routed.Response;
        return response.IsSuccessStatusCode
            ? request.Decide(routed.FileFacts.Media ?? routed.Lease.Media, response.Content.Headers.ContentLength)
            : request.Evaluate(null);
    }

    public async Task<IActionResult> OpenAsync(
        HttpContext http,
        ProtocolExecutionContext context,
        string providerId,
        string externalId,
        ProviderAudioQuality quality,
        ProviderAudioDeliveryRequest? delivery = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(context);
        IDisposable? capacity = admission?.TryEnter(context);
        if (admission != null && capacity == null)
        {
            LocalCapacityException.MarkResponse(http.Response);
            throw new LocalCapacityException("provider-stream");
        }
        try
        {
            var timing = PlaybackStartTimings.Current;
            if (timing != null && timings != null && !HttpMethods.IsHead(http.Request.Method))
            {
                timing.Enable();
                http.Response.Headers["X-Allstarr-Playback-Request"] = timing.Id.ToString("N");
                await timings.RecordAsync(timing.Snapshot("opening"));
            }
            if (context.IsOriginalDownload)
            {
                delivery = null;
                quality = ProviderAudioQuality.Any;
                context = context with { AudioDelivery = null };
            }
            else delivery ??= context.AudioDelivery;
            if (delivery?.InvalidTimeOffset == true || delivery?.StartTimeTicks < 0)
                throw new ProviderAudioOffsetException("Invalid audio time offset.", System.Net.HttpStatusCode.BadRequest);
            if (delivery?.RequestError is { } invalid) throw new ProviderAudioProfileException(invalid);
            if (delivery != null) context = context with { AudioDelivery = delivery };
            var timeSeek = delivery?.StartTimeTicks > 0;
            var headOnly = HttpMethods.IsHead(http.Request.Method);
            var routed = await providers.OpenStreamAsync(
                context,
                providerId,
                externalId,
                quality,
                !timeSeek && http.Request.Headers.Range.ToString() is { Length: > 0 } range ? range : null,
                headOnly && !timeSeek);
            if (routed == null)
            {
                return new ObjectResult(new { error = "No verified playback source is available." })
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable
                };
            }
            if (!routed.Response.IsSuccessStatusCode)
            {
                var status = (int)routed.Response.StatusCode;
                routed.Response.Dispose();
                return new StatusCodeResult(status);
            }

            var sourceMedia = routed.FileFacts.Media ?? routed.Lease.Media;
            var conversion = delivery == null ? null : ProviderAudioTranscoder.Plan(delivery, sourceMedia);
            if (conversion != null && routed.Response.StatusCode == HttpStatusCode.PartialContent && !headOnly)
            {
                var previous = routed;
                try
                {
                    routed = cache == null && previous.OpenOriginalResponseAsync != null
                        ? previous with { Response = await previous.OpenOriginalResponseAsync(http.RequestAborted) }
                        : await providers.OpenStreamAsync(context, providerId, externalId, quality, null, false);
                }
                finally { previous.Response.Dispose(); }
                if (routed == null || !routed.Response.IsSuccessStatusCode)
                {
                    routed?.Response.Dispose();
                    return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
                }
                sourceMedia = PrefetchedStream.Facts(routed.Response.Content)?.Media ?? routed.FileFacts.Media ?? routed.Lease.Media;
                conversion = ProviderAudioTranscoder.Plan(delivery!, sourceMedia);
            }
            if (delivery?.Decide(sourceMedia) is { Supported: false } rejected)
            {
                routed.Response.Dispose();
                throw new ProviderAudioProfileException(rejected.Error!);
            }

            var servingId = routed.ServingExternalId ?? externalId;
            Task<allstarr.Models.Domain.Song?>? metadataTask = null;
            Task<allstarr.Models.Domain.Song?> Metadata() => metadataTask ??= providers.GetSongAsync(
                context.IsOriginalDownload ? context with { IsReadOnly = true } : context, routed.ServingProviderId, servingId);
            try
            {
                var itemId = context.RequestedItemId ?? $"ext-{providerId}-song-{externalId}";
                void Selected(string rung) => activity?.SeekRungSelected(context, itemId, routed.EffectiveQuality, routed, rung);
                if (!context.IsOriginalDownload && (!headOnly || timeSeek)) StreamDurationGuidance.Attach(routed, Metadata, Selected);
                if (cache != null)
                    await cache.WrapAsync(routed, context, routed.ServingProviderId, servingId, headOnly && !timeSeek, Metadata, http.RequestAborted);
                if (!headOnly && (conversion != null || timeSeek))
                {
                    await ManagedTrackCacheService.PrepareForConversionAsync(routed.Response.Content, http.RequestAborted, useDownloadCompletion: false);
                    sourceMedia = routed.FileFacts.Media ?? PrefetchedStream.Facts(routed.Response.Content)?.Media ?? routed.Lease.Media;
                    conversion = ProviderAudioTranscoder.Plan(delivery!, sourceMedia);
                    if (delivery!.Decide(sourceMedia) is { Supported: false } incompatible)
                        throw new ProviderAudioProfileException(incompatible.Error!);
                }
                var cacheRung = ManagedTrackCacheService.SeekRung(routed.Response.Content);
                if (!context.IsOriginalDownload && !headOnly && cacheRung != null) Selected(cacheRung);
                if (conversion != null)
                {
                    var duration = routed.FileFacts.Measured?.DurationSeconds;
                    if (duration == null && routed.Response.Headers.TryGetValues("X-Allstarr-Duration-Seconds", out var durations))
                    {
                        var values = durations.ToArray();
                        if (values.Length == 1 && double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0)
                            duration = seconds;
                    }
                    if (headOnly) ProviderAudioTranscoder.ApplyHead(routed.Response, http.Response, conversion, delivery!.StartTimeTicks, duration);
                    else await ProviderAudioTranscoder.ApplyAsync(routed.Response, http.Response, conversion, conversions, http.RequestAborted, delivery!.StartTimeTicks, duration);
                    Selected("transcoded");
                }
                else if (timeSeek)
                {
                    await ProviderAudioTimeSeek.ApplyAsync(routed.Response, http.Response, delivery!.StartTimeTicks, conversions, http.RequestAborted,
                        routed.FileFacts.Media ?? routed.Lease.Media, routed.FileFacts.Measured?.DurationSeconds);
                    Selected("time-seek");
                }
                var result = await responses.CreateAsync(http, routed.Response, http.RequestAborted,
                    enableRangeProcessing: conversion == null && (timeSeek || routed.IsCached || cacheRung != null), providerStream: true, responseLease: capacity);
                capacity = null;
                return result;
            }
            catch
            {
                routed.Response.Dispose();
                throw;
            }
        }
        catch (Exception error) when (error is ProviderAudioProfileException or ProviderAudioOffsetException)
        {
            var reason = ProblemReportText.Clean(error.Message);
            Activity.Current?.SetTag("audio.rejection_reason", reason);
            activity?.StreamRejected(context, context.RequestedItemId ?? $"ext-{providerId}-song-{externalId}", reason);
            logger?.LogWarning("Provider stream rejected: {Reason}; client={Client}; device={Device}; containers={Containers}; codecs={Codecs}; maxBitrate={MaxBitrate}; maxSampleRate={MaxSampleRate}; sampleRate={SampleRate}; maxChannels={MaxChannels}; channels={Channels}; maxBitDepth={MaxBitDepth}; startTimeTicks={StartTimeTicks}",
                reason, ProblemReportText.Clean(context.Client.ClientId, 200), ProblemReportText.Clean(context.Client.DeviceName, 200),
                ProblemReportText.Clean(string.Join(",", (delivery?.Formats ?? delivery?.Profiles ?? []).SelectMany(profile => profile.Containers))),
                ProblemReportText.Clean(string.Join(",", (delivery?.Formats ?? delivery?.Profiles ?? []).SelectMany(profile => profile.Codecs))),
                delivery?.MaxBitrate, delivery?.MaxSampleRate, delivery?.SampleRate, delivery?.MaxChannels, delivery?.Channels, delivery?.MaxBitDepth, delivery?.StartTimeTicks);
            throw;
        }
        catch (LocalCapacityException)
        {
            LocalCapacityException.MarkResponse(http.Response);
            throw;
        }
        finally { capacity?.Dispose(); }
    }
}
