using System.Net;
using allstarr.Services.Common;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.Music;

public sealed class ProviderHlsSessions(IProtocolProviderGateway providers, TimeProvider clock, ProviderStreamAdmission? admission = null) : IDisposable
{
    private const int MaximumSourceBytes = 32 * 1024 * 1024;
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
            var routed = await providers.OpenStreamAsync(context with { IsReadOnly = true, RequestedItemId = itemId, AudioDelivery = delivery },
                provider, externalId, delivery.Quality, null, false);
            if (routed == null) throw new HttpRequestException("No verified playback source is available.", null, HttpStatusCode.ServiceUnavailable);
            using var response = routed.Response;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("The HLS source could not be opened.", null, response.StatusCode);
            if (delivery.Evaluate(routed.Lease.Media) is { Supported: false } rejected)
                throw new ProviderAudioProfileException(rejected.Error!);
            if (response.Content.Headers.ContentLength > MaximumSourceBytes)
                throw new ProviderAudioProfileException("The source exceeds the supported single-segment HLS size.");
            using var buffer = new MemoryStream();
            var source = await response.Content.ReadAsStreamAsync(context.CancellationToken).WaitAsync(context.CancellationToken);
            var chunk = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(chunk, context.CancellationToken)) != 0)
            {
                if (buffer.Length + count > MaximumSourceBytes)
                    throw new ProviderAudioProfileException("The source exceeds the supported single-segment HLS size.");
                buffer.Write(chunk, 0, count);
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            var bytes = buffer.ToArray();
            var layout = FragmentedAudioLayout.Read(bytes);
            if (startSeconds < 0 || startSeconds >= layout.Duration)
                throw new ProviderAudioProfileException("The requested HLS start position is outside the audio timeline.");
            var media = routed.Lease.Media;
            var verified = new ProviderMediaFormat("audio/mp4", "mp4", "aac", media.Bitrate, layout.SampleRate, media.BitDepth, layout.Channels ?? media.Channels);
            if (delivery.Evaluate(verified) is { Supported: false } incompatible)
                throw new ProviderAudioProfileException(incompatible.Error!);
            var session = new ProviderHlsSession(Guid.NewGuid().ToString("N"), itemId, context.BackendInstanceId,
                context.VerifiedBackendPrincipalId, clock.GetUtcNow().AddMinutes(30), bytes, layout.InitializationLength, layout.Duration);
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
    DateTimeOffset ExpiresAt, byte[] Bytes, int InitializationLength, double Duration);
