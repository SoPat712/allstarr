using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Models.Lyrics;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.ClientEvents;

public sealed class ClientLyricsUpdates(IClientEventPublisher events) : IDisposable
{
    private readonly object _gate = new();
    private readonly MemoryCache _observed = new(new MemoryCacheOptions { SizeLimit = 4096 });

    public void Observe(Guid owner, string itemId, LyricsInfo? lyrics)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { lyrics?.Source, lyrics?.Revision, lyrics?.PlainLyrics, lyrics?.SyncedLyrics, lyrics?.TimedLines }))));
        var changed = false;
        lock (_gate)
        {
            var key = (owner, itemId);
            changed = _observed.TryGetValue<string>(key, out var previous) && previous != revision;
            _observed.Set(key, revision, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) });
        }
        if (changed) events.Publish(owner, ClientEvent.Lyrics(itemId, revision));
    }

    public void Dispose() => _observed.Dispose();
}
