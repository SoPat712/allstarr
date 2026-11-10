using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Services.Local;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private const int PrefetchCapacity = 64;
    private readonly object prefetchGate = new();
    private int prefetchScans;
    private readonly Dictionary<string, Queue<(string Key, ProtocolExecutionContext Context, string Id)>> prefetchQueues = new();
    private readonly Dictionary<string, int> prefetchWorkers = new();
    private readonly HashSet<string> prefetchPending = new(StringComparer.Ordinal);

    internal void QueueAlbumPrefetch(ProtocolExecutionContext protocol, IEnumerable<string> albumIds)
    {
        if (applicationCache?.IsEnabled != true || protocol.Actor == null) return;
        lock (prefetchGate) if (prefetchPending.Count >= PrefetchCapacity) return;
        if (Interlocked.Increment(ref prefetchScans) > 2)
        {
            Interlocked.Decrement(ref prefetchScans);
            return;
        }
        var ids = albumIds.ToArray();
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(async () =>
            {
                try { await QueueAlbumPrefetchAsync(protocol, ids); }
                finally { Interlocked.Decrement(ref prefetchScans); }
            });
    }

    internal async Task QueueAlbumPrefetchAsync(ProtocolExecutionContext protocol, IEnumerable<string> albumIds)
    {
        try
        {
            var background = BackgroundMetadataContext(protocol);
            var queued = 0;
            foreach (var albumId in albumIds.Distinct(StringComparer.Ordinal))
            {
                var parsed = LocalLibraryService.ParseExternalResource(albumId);
                if (!parsed.isExternal || parsed.type != "album") continue;
                var provider = NormalizeProvider(parsed.provider!);
                if (await ReadStoredMetadataAsync<Album>(background, provider, ProviderResourceKind.Album, parsed.externalId!, true) != null) continue;
                if (queued++ == 10) return;
                var key = MetadataCacheKey(background.RequireActor(), provider, ProviderResourceKind.Album, parsed.externalId!, true);
                lock (prefetchGate)
                {
                    if (prefetchPending.Contains(key)) continue;
                    if (prefetchPending.Count >= PrefetchCapacity) return;
                    if (!prefetchQueues.TryGetValue(provider, out var queue)) prefetchQueues[provider] = queue = new();
                    prefetchPending.Add(key);
                    queue.Enqueue((key, background, parsed.externalId!));
                    var workers = prefetchWorkers.GetValueOrDefault(provider);
                    if (workers < 2)
                    {
                        prefetchWorkers[provider] = workers + 1;
                        _ = Task.Run(() => DrainPrefetchAsync(provider));
                    }
                }
            }
        }
        catch (Exception error) { logger?.LogDebug("Album prefetch was unavailable ({ErrorType})", error.GetType().Name); }
    }

    private async Task DrainPrefetchAsync(string provider)
    {
        while (true)
        {
            (string Key, ProtocolExecutionContext Context, string Id) work;
            lock (prefetchGate)
            {
                if (!prefetchQueues[provider].TryDequeue(out work))
                {
                    prefetchWorkers[provider]--;
                    return;
                }
            }
            try
            {
                while (foregroundMetadata.GetValueOrDefault(provider) > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(25), timeProvider ?? TimeProvider.System);
                await GetAlbumCoreAsync(BackgroundMetadataContext(work.Context), provider, work.Id, true);
            }
            catch (Exception error) { logger?.LogDebug("Album prefetch was unavailable ({ErrorType})", error.GetType().Name); }
            finally { lock (prefetchGate) prefetchPending.Remove(work.Key); }
        }
    }

    internal int PendingAlbumPrefetches { get { lock (prefetchGate) return prefetchPending.Count; } }
}
