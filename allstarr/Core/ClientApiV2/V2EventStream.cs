using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using allstarr.Core.ClientEvents;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;

namespace allstarr.Core.ClientApiV2;

public sealed record V2StreamEvent(string Id, ClientEvent Value, DateTimeOffset At);

public sealed class V2EventSubscription(ClientEventSubscription slot, Action remove) : IDisposable
{
    internal Channel<V2StreamEvent> Events { get; } = Channel.CreateBounded<V2StreamEvent>(1001);
    public async Task<V2StreamEvent?> ReadAsync(CancellationToken token) =>
        await Events.Reader.WaitToReadAsync(token) && Events.Reader.TryRead(out var value) ? value : null;
    public void Dispose() { remove(); slot.Dispose(); Events.Writer.TryComplete(); }
}

/// <summary>Retains native payloads briefly, including while a listener's phone is disconnected.</summary>
public sealed class V2EventStream : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Listener> listeners = [];
    private readonly Channel<(Guid Owner, Listener Listener, long Generation, ClientEvent Value)> pending =
        Channel.CreateBounded<(Guid, Listener, long, ClientEvent)>(1000);
    private readonly ClientEventHub hub;
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider clock;
    private readonly ILogger<V2EventStream> logger;
    private long sequence = DateTimeOffset.UtcNow.UtcTicks;

    private sealed class Listener(ProtocolExecutionContext execution, string authorization, DateTimeOffset now)
    {
        public ProtocolExecutionContext Execution = execution;
        public string Authorization = authorization;
        public DateTimeOffset DisconnectedAt = now;
        public long Generation;
        public Queue<V2StreamEvent> Buffer { get; } = new();
        public HashSet<V2EventSubscription> Subscriptions { get; } = [];
    }

    public V2EventStream(ClientEventHub hub, IServiceScopeFactory scopes, TimeProvider clock, ILogger<V2EventStream> logger)
    {
        this.hub = hub;
        this.scopes = scopes;
        this.clock = clock;
        this.logger = logger;
        hub.Published += Queue;
    }

    public V2EventSubscription? Subscribe(HttpContext http, ProtocolExecutionContext execution, string? lastId)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        lock (gate)
        {
            RemoveExpired();
            var slot = hub.Subscribe(owner, execution.Client.DeviceId);
            if (slot == null) return null;
            if (!listeners.TryGetValue(owner, out var listener))
            {
                listener = new(execution, http.Request.Headers.Authorization.ToString(), clock.GetUtcNow());
                listeners.Add(owner, listener);
                hub.RetainListener(owner);
            }
            listener.Execution = execution;
            listener.Authorization = http.Request.Headers.Authorization.ToString();
            V2EventSubscription subscription = null!;
            subscription = new(slot, () =>
            {
                lock (gate)
                    if (listener.Subscriptions.Remove(subscription) && listener.Subscriptions.Count == 0)
                        listener.DisconnectedAt = clock.GetUtcNow();
            });
            Trim(listener);
            if (!string.IsNullOrEmpty(lastId))
            {
                if (listener.Buffer.Any(item => item.Id == lastId))
                {
                    foreach (var item in listener.Buffer.SkipWhile(item => item.Id != lastId).Skip(1))
                        subscription.Events.Writer.TryWrite(item);
                }
                else subscription.Events.Writer.TryWrite(Store(listener, ClientEvent.Create("resync", "", new { })));
            }
            listener.Subscriptions.Add(subscription);
            return subscription;
        }
    }

    internal void Publish(Guid owner, ClientEvent value)
    {
        lock (gate)
        {
            if (!listeners.TryGetValue(owner, out var listener)) return;
            var item = Store(listener, value);
            foreach (var subscription in listener.Subscriptions)
                if (!subscription.Events.Writer.TryWrite(item)) subscription.Events.Writer.TryComplete();
        }
    }

    public void Renamed(Guid oldId, Guid newId)
    {
        lock (gate)
            foreach (var owner in listeners.Where(pair => pair.Value.Subscriptions.Count > 0).Select(pair => pair.Key))
                Publish(owner, ClientEvent.Create("track.renamed", oldId.ToString("D"), new { old = V2Ids.Track(oldId), @new = V2Ids.Track(newId) }));
    }

    private V2StreamEvent Store(Listener listener, ClientEvent value)
    {
        var item = new V2StreamEvent((++sequence).ToString(CultureInfo.InvariantCulture), value, clock.GetUtcNow());
        listener.Buffer.Enqueue(item);
        Trim(listener);
        return item;
    }

    private void Trim(Listener listener)
    {
        while (listener.Buffer.TryPeek(out var first) && (listener.Buffer.Count > 1000 || clock.GetUtcNow() - first.At >= Retention))
            listener.Buffer.Dequeue();
    }

    internal void RemoveExpired()
    {
        lock (gate)
            foreach (var pair in listeners.ToArray())
            {
                Trim(pair.Value);
                if (pair.Value.Subscriptions.Count != 0 || clock.GetUtcNow() - pair.Value.DisconnectedAt < Retention) continue;
                listeners.Remove(pair.Key);
                hub.ForgetListener(pair.Key);
            }
    }

    private void Queue(Guid owner, ClientEvent value)
    {
        if (value.Type is not ("keep.updated" or "match.updated" or "lyrics.updated" or "mix.updated" or "releases.updated" or "libraryAddition.updated")) return;
        lock (gate)
            if (listeners.TryGetValue(owner, out var listener) && !pending.Writer.TryWrite((owner, listener, listener.Generation, value)))
            {
                listener.Generation++;
                Publish(owner, ClientEvent.Create("resync", "", new { }));
            }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleanup = CleanupAsync(stoppingToken);
        try
        {
            await foreach (var work in pending.Reader.ReadAllAsync(stoppingToken))
            {
                ProtocolExecutionContext execution;
                string authorization;
                lock (gate)
                {
                    if (!listeners.TryGetValue(work.Owner, out var current) || current != work.Listener || current.Generation != work.Generation) continue;
                    execution = current.Execution;
                    authorization = current.Authorization;
                }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await using var scope = scopes.CreateAsyncScope();
                    var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = timeout.Token };
                    http.Request.Headers.Authorization = authorization;
                    var context = new ProtocolExecutionContext(execution.Protocol, execution.BackendInstanceId,
                        execution.VerifiedBackendPrincipalId, execution.Principal, execution.CorrelationId,
                        clock.GetUtcNow().AddSeconds(15), timeout.Token, execution.Client);
                    http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = context;
                    var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
                    accessor.HttpContext = http;
                    try
                    {
                        var values = await ProjectAsync(work.Value, http, context);
                        lock (gate)
                            if (listeners.GetValueOrDefault(work.Owner) == work.Listener && work.Listener.Generation == work.Generation)
                                foreach (var value in values) Publish(work.Owner, value);
                    }
                    finally { accessor.HttpContext = null; }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Could not publish a native client update");
                    Publish(work.Owner, ClientEvent.Create("resync", "", new { }));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await cleanup; }
    }

    private async Task CleanupAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), clock, token);
                RemoveExpired();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task<ClientEvent[]> ProjectAsync(ClientEvent value, HttpContext http, ProtocolExecutionContext execution)
    {
        using var document = JsonDocument.Parse(value.Json);
        var data = document.RootElement;
        if (value.Type is "keep.updated" or "match.updated")
        {
            var result = await http.RequestServices.GetRequiredService<V2TrackReader>().ReadAsync(http, execution, [value.Key], false);
            var track = result.Tracks.FirstOrDefault();
            if (track == null) return [ClientEvent.Create("resync", "", new { })];
            return
            [
                ClientEvent.Create("track.updated", track.Id, new { track }),
                value.Type == "keep.updated"
                    ? ClientEvent.Create(value.Type, track.Id, new
                    {
                        trackId = track.Id,
                        state = data.GetProperty("state").GetString(),
                        progress = data.TryGetProperty("progress", out var progress) ? progress.GetDouble() : (double?)null
                    })
                    : ClientEvent.Create(value.Type, track.Id, new { trackId = track.Id, state = data.GetProperty("state").GetString() })
            ];
        }
        else if (value.Type == "lyrics.updated")
        {
            var recording = await http.RequestServices.GetRequiredService<ITrackIdentityService>()
                .ResolveRecordingAsync(execution.RequireActor(), value.Key, execution.CancellationToken);
            var trackId = recording == null ? V2Ids.TrackSource(value.Key) : V2Ids.Track(recording.Id);
            return [ClientEvent.Create(value.Type, trackId, new { trackId, revision = data.GetProperty("revision") })];
        }
        else if (value.Type == "mix.updated")
            return
            [
                ClientEvent.Create(value.Type, value.Key, new
                {
                    mixId = V2Ids.Mix(data.GetProperty("mixId").GetGuid()),
                    playlistId = V2Ids.Playlist(data.GetProperty("playlistId").GetString()!)
                })
            ];
        else if (value.Type == "libraryAddition.updated") return [ClientEvent.Create(value.Type, value.Key, new { row = data })];
        else return [ClientEvent.Create(value.Type, value.Key, new { count = data.GetProperty("count").GetInt32() })];
    }

    public override void Dispose()
    {
        hub.Published -= Queue;
        base.Dispose();
        lock (gate)
        {
            foreach (var pair in listeners)
            {
                foreach (var subscription in pair.Value.Subscriptions.ToArray()) subscription.Dispose();
                hub.ForgetListener(pair.Key);
            }
            listeners.Clear();
        }
    }
}
