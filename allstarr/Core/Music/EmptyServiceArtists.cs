using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

/// <summary>
/// Service artists whose page turned out to have no albums, remembered for a day so search can leave them out.
/// Search looks at same-named service artists in the background (cached album lists, a few per search), so a
/// later search no longer lists an artist that opens to nothing.
/// </summary>
public sealed class EmptyServiceArtists(TimeProvider? clock = null)
{
    private const int Kept = 4096, ProbesPerSearch = 6;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, bool Empty)> known = new(StringComparer.Ordinal);
    private readonly HashSet<string> probing = new(StringComparer.Ordinal);

    public bool IsEmpty(Artist artist)
    {
        if (Key(artist) is not { } key) return false;
        lock (known) return known.TryGetValue(key, out var entry) && entry.Empty && entry.ExpiresAt > Now;
    }

    public void Record(string provider, string artistId, int albumCount)
    {
        lock (known)
        {
            if (known.Count >= Kept && !known.ContainsKey(Key(provider, artistId)))
                known.Remove(known.MinBy(item => item.Value.ExpiresAt).Key);
            known[Key(provider, artistId)] = (Now + Lifetime, albumCount == 0);
        }
    }

    /// <summary>Starts looking at service artists that share a name with another listed artist; results come in later.</summary>
    public Task Probe(IProtocolProviderGateway providers, ProtocolExecutionContext context, IEnumerable<Artist> service,
        IEnumerable<string>? libraryNames = null)
    {
        var artists = service.Where(artist => Key(artist) != null).ToArray();
        var names = artists.GroupBy(artist => FuzzyMatcher.NormalizeForMatching(artist.Name))
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .Concat((libraryNames ?? []).Select(FuzzyMatcher.NormalizeForMatching)).ToHashSet(StringComparer.Ordinal);
        var unknown = new List<Artist>();
        lock (known)
            foreach (var artist in artists.Where(artist => names.Contains(FuzzyMatcher.NormalizeForMatching(artist.Name))))
            {
                var key = Key(artist)!;
                if (unknown.Count >= ProbesPerSearch) break;
                if (known.TryGetValue(key, out var entry) && entry.ExpiresAt > Now || !probing.Add(key)) continue;
                unknown.Add(artist);
            }
        return Task.WhenAll(unknown.Select(artist => LookAsync(providers, context, artist)));
    }

    private async Task LookAsync(IProtocolProviderGateway providers, ProtocolExecutionContext context, Artist artist)
    {
        var key = Key(artist)!;
        try { Record(artist.ExternalProvider!, artist.ExternalId!, (await providers.GetArtistAlbumsAsync(context, artist.ExternalProvider!, artist.ExternalId!)).Count); }
        // A service that fails says nothing about the artist.
        catch (Exception) { }
        finally { lock (known) probing.Remove(key); }
    }

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private static string Key(string provider, string artistId) => AppleMusicClient.NormalizeProviderId(provider) + ":" + artistId;
    private static string? Key(Artist artist) => string.IsNullOrEmpty(artist.ExternalProvider) || string.IsNullOrEmpty(artist.ExternalId)
        ? null : Key(artist.ExternalProvider, artist.ExternalId);
}
