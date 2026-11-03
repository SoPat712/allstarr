using System.Threading.Channels;

namespace allstarr.Core.ClientEvents;

public sealed class ClientEventHub(ClientEventOptions options) : IClientEventPublisher, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<ClientEventSubscription> _subscriptions = [];
    private bool _disposed;
    public int ConnectionCount { get { lock (_gate) return _subscriptions.Count; } }
    public Guid[] Listeners { get { lock (_gate) return _subscriptions.Select(item => item.UserId).Distinct().ToArray(); } }

    public ClientEventSubscription? Subscribe(Guid userId, string? deviceId)
    {
        lock (_gate)
        {
            if (_disposed || _subscriptions.Count >= options.MaximumConnections ||
                _subscriptions.Count(item => item.UserId == userId) >= options.ConnectionsPerUser) return null;
            var subscription = new ClientEventSubscription(userId, deviceId, options, Remove);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public void Publish(Guid userId, ClientEvent value, string? deviceId = null)
    {
        if (value.Type == "playback.updated" && string.IsNullOrEmpty(deviceId)) return;
        ClientEventSubscription[] targets;
        lock (_gate) targets = _subscriptions.Where(item => item.UserId == userId &&
            (deviceId == null || item.DeviceId == deviceId)).ToArray();
        foreach (var target in targets) target.Publish(value);
    }

    private void Remove(ClientEventSubscription subscription) { lock (_gate) _subscriptions.Remove(subscription); }
    public void Dispose()
    {
        ClientEventSubscription[] subscriptions;
        lock (_gate) { _disposed = true; subscriptions = _subscriptions.ToArray(); _subscriptions.Clear(); }
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}

public sealed class ClientEventSubscription : IDisposable
{
    private readonly object _gate = new();
    private readonly Channel<string> _keys;
    private readonly Dictionary<string, ClientEvent> _pending = [];
    private readonly ClientEventOptions _options;
    private readonly Action<ClientEventSubscription> _remove;
    private long _lastSent;
    private bool _disposed;
    public Guid UserId { get; }
    public string? DeviceId { get; }
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    internal ClientEventSubscription(Guid userId, string? deviceId, ClientEventOptions options, Action<ClientEventSubscription> remove)
    {
        UserId = userId;
        DeviceId = deviceId;
        _options = options;
        _remove = remove;
        _keys = Channel.CreateBounded<string>(new BoundedChannelOptions(options.Capacity)
        { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }

    internal void Publish(ClientEvent value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var key = value.Type + ":" + value.Key;
            if (_pending.ContainsKey(key)) { _pending[key] = value; return; }
            if (_pending.Count >= _options.Capacity && _keys.Reader.TryRead(out var oldest)) _pending.Remove(oldest);
            _pending[key] = value;
            _keys.Writer.TryWrite(key);
        }
    }

    public async Task<ClientEvent?> ReadAsync(CancellationToken token)
    {
        while (await _keys.Reader.WaitToReadAsync(token))
        {
            var elapsed = _lastSent == 0 ? TimeSpan.MaxValue : System.Diagnostics.Stopwatch.GetElapsedTime(_lastSent);
            var rateDelay = TimeSpan.FromSeconds(1d / _options.EventsPerSecond) - elapsed;
            var delay = rateDelay > _options.CoalesceWindow ? rateDelay : _options.CoalesceWindow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            lock (_gate)
            {
                if (_disposed) return null;
                if (_keys.Reader.TryRead(out var key) && _pending.Remove(key, out var value))
                {
                    _lastSent = System.Diagnostics.Stopwatch.GetTimestamp();
                    return value;
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _pending.Clear(); _keys.Writer.TryComplete(); }
        _remove(this);
    }
}
