using System.Collections.Concurrent;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;

namespace allstarr.Core.ClientApiV2;

/// <summary>Facts captured when a native stream starts serving, retained only while its response is active.</summary>
public sealed class V2ServingStreams
{
    private readonly record struct Key(Guid Listener, string Backend, string Principal, string? Device, string Track);
    private sealed class Observation(V2ServingFacts facts) { internal V2ServingFacts Facts { get; } = facts; }
    private readonly ConcurrentDictionary<Key, Observation> streams = new();

    private static Key Scope(ProtocolExecutionContext execution, string track) =>
        new(execution.Principal!.UserId, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId, execution.Client.DeviceId, track);

    internal V2ServingFacts? Read(ProtocolExecutionContext execution, string track) => streams.GetValueOrDefault(Scope(execution, track))?.Facts;

    internal void Observe(HttpContext http, ProtocolExecutionContext execution, string track, ProviderMediaFormat media, string source)
    {
        if (HttpMethods.IsHead(http.Request.Method)) return;
        var key = Scope(execution, track);
        var observation = new Observation(new(media.Codec, media.Bitrate, media.BitDepth, media.SampleRate, source));
        void Remove() => ((ICollection<KeyValuePair<Key, Observation>>)streams).Remove(new(key, observation));
        http.Response.OnStarting(() =>
        {
            if (http.Response.StatusCode is >= 200 and < 300 && !http.RequestAborted.IsCancellationRequested) streams[key] = observation;
            return Task.CompletedTask;
        });
        http.Response.RegisterForDispose(http.RequestAborted.Register(Remove));
        http.Response.OnCompleted(() => { Remove(); return Task.CompletedTask; });
    }
}
