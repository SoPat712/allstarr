namespace allstarr.Core.Providers.Lyrics;

internal sealed class PaxsenixFailureCache(TimeProvider clock, int capacity = 1024)
{
    internal static readonly PaxsenixFailureCache Shared = new(TimeProvider.System);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, LinkedListNode<(string Id, DateTimeOffset Expires)>> _ids = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Id, DateTimeOffset Expires)> _order = new();
    private readonly object _gate = new();

    public bool Contains(string id)
    {
        lock (_gate)
        {
            if (!_ids.TryGetValue(id, out var entry)) return false;
            if (entry.Value.Expires > clock.GetUtcNow()) return true;
            _ids.Remove(id);
            _order.Remove(entry);
            return false;
        }
    }

    public void Remember(string id)
    {
        lock (_gate)
        {
            if (_ids.Remove(id, out var previous)) _order.Remove(previous);
            while (_ids.Count >= capacity && _order.First is { } oldest)
            {
                _ids.Remove(oldest.Value.Id);
                _order.RemoveFirst();
            }
            _ids[id] = _order.AddLast((id, clock.GetUtcNow().Add(Lifetime)));
        }
    }
}
