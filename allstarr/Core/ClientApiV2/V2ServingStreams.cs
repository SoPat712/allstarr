using System.Collections.Concurrent;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.ClientApiV2;

/// <summary>Resolved playback ownership and facts captured while each stream response is active.</summary>
public sealed class V2ServingStreams : IDisposable
{
    private readonly record struct Key(Guid Listener, string Backend, string Principal, string? Device, string Track);
    private readonly record struct PlaybackKey(Guid Listener, string Backend, string Principal, string Id);
    private sealed class Playback
    {
        internal ConcurrentDictionary<Observation, byte> Active { get; } = new();
    }
    private sealed class Observation(V2ServingFacts facts, V2PlaybackFormat delivered)
    {
        internal V2ServingFacts Facts { get; } = facts;
        internal V2PlaybackFormat Delivered { get; } = delivered;
    }
    private readonly ConcurrentDictionary<Key, Observation> streams = new();
    private readonly MemoryCache playbacks = new(new MemoryCacheOptions { SizeLimit = 4096 });

    private static Key Scope(ProtocolExecutionContext execution, string track) =>
        new(execution.Principal!.UserId, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId, execution.Client.DeviceId, track);

    internal V2ServingFacts? Read(ProtocolExecutionContext execution, string track) => streams.GetValueOrDefault(Scope(execution, track))?.Facts;

    private static PlaybackKey PlaybackScope(ProtocolExecutionContext execution, string id) =>
        new(execution.Principal!.UserId, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId, id);

    internal void Remember(ProtocolExecutionContext execution, string id, DateTimeOffset expires) =>
        playbacks.Set(PlaybackScope(execution, id), new Playback(), new MemoryCacheEntryOptions { AbsoluteExpiration = expires, Size = 1 });

    public V2Result Status(ProtocolExecutionContext execution, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return new(400, null);
        if (playbacks.Get<Playback>(PlaybackScope(execution, id)) is not { } playback) return new(404, null);
        var active = playback.Active.Keys.FirstOrDefault();
        return new(200, new V2PlaybackStatus(id, active?.Delivered, active?.Facts));
    }

    internal void Observe(HttpContext http, ProtocolExecutionContext execution, string track, ProviderMediaFormat media, string source)
    {
        if (HttpMethods.IsHead(http.Request.Method)) return;
        var key = Scope(execution, track);
        var observation = new Observation(new(media.Codec, media.Bitrate, media.BitDepth, media.SampleRate, source), V2PlaybackReader.Format(media));
        var playback = playbacks.Get<Playback>(PlaybackScope(execution, execution.CorrelationId));
        void Remove()
        {
            ((ICollection<KeyValuePair<Key, Observation>>)streams).Remove(new(key, observation));
            playback?.Active.TryRemove(observation, out _);
        }
        http.Response.OnStarting(() =>
        {
            if (http.Response.StatusCode is >= 200 and < 300 && !http.RequestAborted.IsCancellationRequested)
            {
                streams[key] = observation;
                if (playback != null) playback.Active[observation] = 0;
            }
            return Task.CompletedTask;
        });
        http.Response.RegisterForDispose(http.RequestAborted.Register(Remove));
        http.Response.OnCompleted(() => { Remove(); return Task.CompletedTask; });
    }

    public void Dispose() => playbacks.Dispose();
}
