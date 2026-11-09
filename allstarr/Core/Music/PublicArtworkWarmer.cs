using System.Collections.Concurrent;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Core.Music;

// Apps load song pictures without a sign-in, as Jellyfin allows, and such a request only reads covers Allstarr already
// knows. A song's cover becomes known once it is read for some listener, so mixes and synced playlists read their new
// songs that way as soon as they are built, a few at a time in the background.
public sealed class PublicArtworkWarmer(IProtocolProviderGateway providers, ILogger<PublicArtworkWarmer>? logger = null)
{
    private const int MaximumSongs = 300;
    private static readonly TimeSpan Remembered = TimeSpan.FromHours(12);
    private readonly SemaphoreSlim _slots = new(4);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    // Returns the background reads, which callers don't wait for.
    public Task Warm(ProtocolExecutionContext source, IEnumerable<Song> songs)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = songs.Where(song => !song.IsLocal && song.ExternalProvider is { Length: > 0 } && song.ExternalId is { Length: > 0 })
            .Select(song => (Provider: song.ExternalProvider!, Id: song.ExternalId!))
            .Distinct()
            .Where(song => !_recent.TryGetValue($"{song.Provider}:{song.Id}", out var at) || now - at > Remembered)
            .Take(MaximumSongs).ToArray();
        if (pending.Length == 0) return Task.CompletedTask;
        foreach (var song in pending) _recent[$"{song.Provider}:{song.Id}"] = now;
        if (_recent.Count > 20_000)
            foreach (var stale in _recent.Where(item => now - item.Value > Remembered).Select(item => item.Key).ToArray())
                _recent.TryRemove(stale, out _);
        // The job that built the mix may finish first, so the reads run with their own time limit and no cancellation.
        var context = new ProtocolExecutionContext(source.Protocol, source.BackendInstanceId, source.VerifiedBackendPrincipalId,
            source.Principal, "artwork-warmup", now.AddMinutes(30), CancellationToken.None)
        { Policy = source.Policy };
        return Task.Run(() => WarmAsync(context, pending));
    }

    private async Task WarmAsync(ProtocolExecutionContext context, IReadOnlyList<(string Provider, string Id)> songs)
    {
        var warmed = 0;
        foreach (var (provider, id) in songs)
        {
            await _slots.WaitAsync();
            try
            {
                // Covers anyone can already load are left alone; only stored covers are checked, nothing is looked up.
                if (await providers.GetPublicArtworkUriAsync(provider, ProviderResourceKind.Track, id, storedOnly: true) != null) continue;
                if (await providers.GetSongAsync(context, provider, id) != null) warmed++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger?.LogDebug(exception, "Cover warm-up skipped a song from {Provider}", provider);
            }
            finally { _slots.Release(); }
        }
        if (warmed > 0) logger?.LogInformation("Cover warm-up read {Count} songs so their covers load without sign-in", warmed);
    }
}
