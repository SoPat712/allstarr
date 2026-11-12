using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

/// <summary>
/// Service artists whose page turned out to have no albums, remembered for a day so search can leave them out.
/// Search looks at same-named service artists in the background (cached album lists, a few per search), so a
/// later search no longer lists an artist that opens to nothing. A source whose artist pages are nearly always empty
/// (it finds songs but can't list an artist's albums) has all its artists left out until a day passes and it is
/// checked again.
/// </summary>
public sealed class EmptyServiceArtists(TimeProvider? clock = null)
{
    private const int Kept = 4096, ProbesPerSearch = 6, SourceChecks = 10;
    private const double DeadShare = 0.9;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, bool Empty)> known = new(StringComparer.Ordinal);
    private readonly HashSet<string> probing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Since, int Empty, int Full)> sources = new(StringComparer.Ordinal);

    public bool IsEmpty(Artist artist)
    {
        if (Key(artist) is not { } key) return false;
        lock (known)
            return known.TryGetValue(key, out var entry) && entry.Empty && entry.ExpiresAt > Now || Dead(artist.ExternalProvider!);
    }

    private bool Dead(string provider) => sources.TryGetValue(Source(provider), out var seen) && seen.Since + Lifetime > Now &&
        seen.Empty + seen.Full >= SourceChecks && seen.Empty >= DeadShare * (seen.Empty + seen.Full);

    public void Record(string provider, string artistId, int albumCount)
    {
        lock (known)
        {
            if (known.Count >= Kept && !known.ContainsKey(Key(provider, artistId)))
                known.Remove(known.MinBy(item => item.Value.ExpiresAt).Key);
            known[Key(provider, artistId)] = (Now + Lifetime, albumCount == 0);
            var source = Source(provider);
            var seen = sources.TryGetValue(source, out var current) && current.Since + Lifetime > Now ? current : (Since: Now, Empty: 0, Full: 0);
            sources[source] = albumCount == 0 ? seen with { Empty = seen.Empty + 1 } : seen with { Full = seen.Full + 1 };
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
            foreach (var artist in artists.Where(artist => names.Contains(FuzzyMatcher.NormalizeForMatching(artist.Name)) &&
                         !Dead(artist.ExternalProvider!)))
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
    private static string Source(string provider) => AppleMusicClient.NormalizeProviderId(provider);
    private static string Key(string provider, string artistId) => Source(provider) + ":" + artistId;
    private static string? Key(Artist artist) => string.IsNullOrEmpty(artist.ExternalProvider) || string.IsNullOrEmpty(artist.ExternalId)
        ? null : Key(artist.ExternalProvider, artist.ExternalId);
}
