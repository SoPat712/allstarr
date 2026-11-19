using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Domain;

namespace allstarr.Core.Music;

/// <summary>
/// Service songs whose own service said they can't be played in full (a preview-only upload, for example), remembered
/// for a day so search stops listing them. Any service can answer this way, so nothing here depends on one of them.
/// </summary>
public sealed class UnplayableSongs(TimeProvider? clock = null)
{
    private const int Kept = 4096;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    private readonly Dictionary<string, DateTimeOffset> known = new(StringComparer.Ordinal);

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public void Record(string provider, string externalId)
    {
        lock (known)
        {
            var key = Key(provider, externalId);
            if (known.Count >= Kept && !known.ContainsKey(key)) known.Remove(known.MinBy(item => item.Value).Key);
            known[key] = Now + Lifetime;
        }
    }

    public bool Contains(Song song)
    {
        if (string.IsNullOrWhiteSpace(song.ExternalProvider) || string.IsNullOrWhiteSpace(song.ExternalId)) return false;
        lock (known)
            return known.TryGetValue(Key(song.ExternalProvider, song.ExternalId), out var until) && until > Now;
    }

    private static string Key(string provider, string externalId) =>
        AppleMusicClient.NormalizeProviderId(provider) + "\u001f" + externalId;
}
