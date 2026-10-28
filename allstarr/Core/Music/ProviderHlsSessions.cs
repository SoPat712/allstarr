using System.Net;
using allstarr.Services.Common;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.Music;

public sealed class ProviderHlsSessions(IProtocolProviderGateway providers, TimeProvider clock, ProviderStreamAdmission? admission = null,
    ManagedTrackCacheService? cache = null, MediaConversionGate? conversions = null) : IDisposable
{
    private const int MaximumSessionBytes = 32 * 1024 * 1024;
    private readonly MemoryCache _sessions = new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    private readonly SemaphoreSlim _opening = new(2);

    public async Task<ProviderHlsSession> CreateAsync(ProtocolExecutionContext context, string itemId,
        string provider, string externalId, ProviderAudioDeliveryRequest delivery, double startSeconds = 0)
    {
        if (delivery.RequestError is { } invalid) throw new ProviderAudioProfileException(invalid);
        using var capacity = admission?.TryEnter(context);
        if (admission != null && capacity == null) throw new LocalCapacityException("provider-stream");
        if (!await _opening.WaitAsync(0, context.CancellationToken)) throw new LocalCapacityException("provider-stream");
        try
        {
            delivery = delivery with { RequireHls = true };
            context = context with { IsReadOnly = false, RequestedItemId = itemId, AudioDelivery = delivery };
            var routed = await providers.OpenStreamAsync(context,
                provider, externalId, delivery.Quality, null, false);
            if (routed == null) throw new HttpRequestException("No verified playback source is available.", null, HttpStatusCode.ServiceUnavailable);
            using var response = routed.Response;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("The HLS source could not be opened.", null, response.StatusCode);
            var media = routed.FileFacts.Media ?? routed.Lease.Media;
            if (delivery.Decide(media) is { Supported: false } rejected)
                throw new ProviderAudioProfileException(rejected.Error!);
            if (cache != null)
                await cache.WrapAsync(routed, context, routed.ServingProviderId, routed.ServingExternalId ?? externalId, false,
                    () => providers.GetSongAsync(context, routed.ServingProviderId, routed.ServingExternalId ?? externalId), context.CancellationToken);
            var conversion = ProviderAudioTranscoder.Plan(delivery, media);
            byte[] bytes;
            FragmentedAudioLayout? layout = null;
            if (conversion != null)
            {
                (bytes, layout, media) = await ConvertAsync(response, conversion, delivery, context.CancellationToken);
            }
            else
            {
                bytes = await ReadBoundedAsync(response.Content, context.CancellationToken);
                var declared = StreamDurationGuidance.DeclaredLength(response.Content) ?? response.Content.Headers.ContentLength;
                if (declared is { } expected && bytes.Length != expected)
                    throw new IOException("The HLS source ended before its declared length.");
                if (media.Codec == "aac" && ProviderAudioDeliveryRequest.Container(media.Container) == "mp4" &&
                    (bytes.AsSpan().IndexOf("moof"u8) >= 0 || bytes.AsSpan().IndexOf("mvex"u8) >= 0))
                {
                    layout = FragmentedAudioLayout.Read(bytes);
                    media = new("audio/mp4", "mp4", "aac", media.Bitrate, layout.SampleRate, media.BitDepth, layout.Channels ?? media.Channels);
                }
                if (layout == null || !delivery.Evaluate(media).Supported)
                {
                    conversion = ProviderAudioTranscoder.Plan(delivery with { EnableDirectPlay = false, EnableDirectStream = false }, media);
                    if (conversion == null) throw new ProviderAudioProfileException("The audio profile does not support AAC fragmented MP4 HLS segments.");
                    response.Content.Dispose();
                    response.Content = new ByteArrayContent(bytes);
                    response.Content.Headers.ContentType = new(media.MimeType);
                    (bytes, layout, media) = await ConvertAsync(response, conversion, delivery, context.CancellationToken);
                }
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            if (layout == null) throw new ProviderAudioProfileException("The HLS audio timeline could not be verified.");
            if (startSeconds < 0 || startSeconds >= layout.Duration)
                throw new ProviderAudioProfileException("The requested HLS start position is outside the audio timeline.");
            var session = new ProviderHlsSession(Guid.NewGuid().ToString("N"), itemId, context.BackendInstanceId,
                context.VerifiedBackendPrincipalId, clock.GetUtcNow().AddMinutes(30), bytes, layout.InitializationLength, layout.Duration, media);
            _sessions.Set(session.Id, session, new MemoryCacheEntryOptions
            {
                Size = bytes.Length,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });
            if (!_sessions.TryGetValue(session.Id, out _))
                throw new LocalCapacityException("provider-stream");
            return session;
        }
        finally { _opening.Release(); }
    }

    private async Task<(byte[] Bytes, FragmentedAudioLayout Layout, ProviderMediaFormat Media)> ConvertAsync(
        HttpResponseMessage response, ProviderAudioConversion conversion, ProviderAudioDeliveryRequest delivery, CancellationToken token)
    {
        if (conversion is not { Media.Codec: "aac", Media.Container: "mp4" })
            throw new ProviderAudioProfileException("The audio profile does not support AAC fragmented MP4 HLS segments.");
        await ProviderAudioTranscoder.ApplyAsync(response, new DefaultHttpContext().Response, conversion, conversions, token);
        var bytes = await ReadBoundedAsync(response.Content, token);
        var layout = FragmentedAudioLayout.Read(bytes);
        var media = new ProviderMediaFormat("audio/mp4", "mp4", "aac", conversion.Media.Bitrate, layout.SampleRate, null, layout.Channels ?? conversion.Media.Channels);
        if (!delivery.AcceptsConversion(media, hls: true))
            throw new ProviderAudioProfileException("The HLS conversion does not meet the requested audio profile.");
        return (bytes, layout, media);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaximumSessionBytes)
            throw new ProviderAudioProfileException("The audio exceeds the supported single-segment HLS size.");
        using var buffer = new MemoryStream();
        var source = await content.ReadAsStreamAsync(token).WaitAsync(token);
        var chunk = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > MaximumSessionBytes)
                throw new ProviderAudioProfileException("The source exceeds the supported single-segment HLS size.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    public ProviderHlsSession? Find(string id)
    {
        if (!_sessions.TryGetValue(id, out ProviderHlsSession? session)) return null;
        if (session!.ExpiresAt > clock.GetUtcNow()) return session;
        _sessions.Remove(id);
        return null;
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _opening.Dispose();
    }
}

public sealed record ProviderHlsSession(string Id, string ItemId, string BackendInstanceId, string PrincipalId,
    DateTimeOffset ExpiresAt, byte[] Bytes, int InitializationLength, double Duration, ProviderMediaFormat? Media = null);
