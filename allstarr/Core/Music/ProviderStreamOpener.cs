using System.Diagnostics;
using allstarr.Core.Capabilities;
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
    ILogger<ProviderStreamOpener>? logger = null)
{
    internal static readonly IReadOnlySet<string> PreparationProviders = new HashSet<string>(StringComparer.Ordinal)
    { "apple-musickit", "apple-download" };

    public async Task PrepareAsync(ProtocolExecutionContext context, string itemId)
    {
        if (!context.IsPreparation || cache?.CanPrepare(context) != true) return;
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
            if (context.AudioDelivery?.Evaluate(routed.Lease.Media) is { Supported: false } rejected)
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
            ? request.Evaluate(routed.Lease.Media, response.Content.Headers.ContentLength)
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
        if (context.IsOriginalDownload)
        {
            delivery = null;
            quality = ProviderAudioQuality.Any;
            context = context with { AudioDelivery = null };
        }
        else delivery ??= context.AudioDelivery;
        if (delivery?.RequestError is { } invalid) throw new ProviderAudioProfileException(invalid);
        if (delivery != null) context = context with { AudioDelivery = delivery };
        var headOnly = HttpMethods.IsHead(http.Request.Method);
        var routed = await providers.OpenStreamAsync(
            context,
            providerId,
            externalId,
            quality,
            http.Request.Headers.Range.ToString() is { Length: > 0 } range ? range : null,
            headOnly);
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

        if (delivery?.Evaluate(routed.Lease.Media) is { Supported: false } rejected)
        {
            routed.Response.Dispose();
            throw new ProviderAudioProfileException(rejected.Error!);
        }

        var servingId = routed.ServingExternalId ?? externalId;
        Task<allstarr.Models.Domain.Song?>? metadataTask = null;
        Task<allstarr.Models.Domain.Song?> Metadata() => metadataTask ??= providers.GetSongAsync(context, routed.ServingProviderId, servingId);
        try
        {
            var itemId = context.RequestedItemId ?? $"ext-{providerId}-song-{externalId}";
            void Selected(string rung) => activity?.SeekRungSelected(context, itemId, routed.EffectiveQuality, routed, rung);
            if (!context.IsOriginalDownload && !headOnly) StreamDurationGuidance.Attach(routed, Metadata, Selected);
            if (cache != null)
                await cache.WrapAsync(routed, context, routed.ServingProviderId, servingId, headOnly, Metadata, http.RequestAborted);
            var cacheRung = ManagedTrackCacheService.SeekRung(routed.Response.Content);
            if (!context.IsOriginalDownload && !headOnly && cacheRung != null) Selected(cacheRung);
            return await responses.CreateAsync(http, routed.Response, http.RequestAborted,
                enableRangeProcessing: routed.IsCached || cacheRung != null, providerStream: true);
        }
        catch
        {
            routed.Response.Dispose();
            throw;
        }
    }
}
