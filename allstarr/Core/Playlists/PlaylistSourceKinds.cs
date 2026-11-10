using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

// Whose a synced playlist is. Playlists you made fill your library lists; ones a service makes for you (Discover Weekly,
// Daily Mix, Top 50) and ones you follow from someone else only show as playlists, unless you choose otherwise.
public static class PlaylistSourceKinds
{
    public const string Own = "own";
    public const string Feed = "feed";
    public const string Followed = "followed";

    private static readonly string[] ServiceCurators = ["Apple Music", "Deezer", "TIDAL", "Qobuz", "YouTube Music"];

    // Null when the owner isn't known yet; an unknown playlist counts as yours.
    public static string? Classify(string providerId, string playlistId, string? ownerId, string? sourceUser)
    {
        if (providerId == "listenbrainz")
            return playlistId.StartsWith("createdfor:", StringComparison.Ordinal) ||
                   playlistId is Providers.ListenBrainz.ListenBrainzPlaylistCapabilityAdapter.Recommended
                       or Providers.ListenBrainz.ListenBrainzPlaylistCapabilityAdapter.FreshReleases ? Feed : null;
        // Last.fm's lists, loved songs included, are what the service keeps for you, not playlists you made.
        if (providerId == "lastfm")
            return playlistId is "loved" or "similar" || playlistId.StartsWith("top:", StringComparison.Ordinal) ? Feed : null;
        // Spotify's own playlists (Discover Weekly, Daily Mix, Release Radar, Top 50, Today's Top Hits) share this prefix,
        // so they are known as feeds before any sync says who owns them.
        if (providerId == "spotify" && playlistId.StartsWith("37i9dQZ", StringComparison.Ordinal)) return Feed;
        var owner = ownerId?.Trim();
        if (string.IsNullOrEmpty(owner) || owner == "unknown-owner" || owner.EndsWith(":unknown", StringComparison.Ordinal)) return null;
        if (owner is "selected-user" or "selected-account") return Own;
        if (IsService(providerId, owner)) return Feed;
        if (string.IsNullOrWhiteSpace(sourceUser)) return null;
        return SameUser(owner, sourceUser.Trim()) ? Own : Followed;
    }

    public static bool IsService(string providerId, string owner) =>
        providerId == "spotify"
            ? owner.Equals("spotify", StringComparison.OrdinalIgnoreCase) || owner.Equals("spotify:user:spotify", StringComparison.OrdinalIgnoreCase)
            : ServiceCurators.Contains(owner, StringComparer.OrdinalIgnoreCase);

    private static bool SameUser(string owner, string user) =>
        owner.Equals(user, StringComparison.OrdinalIgnoreCase) ||
        owner.Equals("spotify:user:" + user, StringComparison.OrdinalIgnoreCase) ||
        user.Equals("spotify:user:" + owner, StringComparison.OrdinalIgnoreCase);

    public static bool CountsTowardLibrary(bool? choice, string? kind) => choice ?? kind is not (Feed or Followed);

    // What apps see: Yours, Feed or Followed.
    public static string Label(string? kind) => kind switch
    {
        Feed => "Feed",
        Followed => "Followed",
        _ => "Yours"
    };

    // The newest known kind for each link. A sync that couldn't tell keeps the last answer, and a link no sync has
    // classified yet uses what its source alone says (Spotify's own playlists, ListenBrainz and Last.fm mixes).
    public static async Task<Dictionary<Guid, string>> LatestAsync(AllstarrDbContext db, IReadOnlyCollection<Guid> linkIds,
        CancellationToken cancellationToken)
    {
        if (linkIds.Count == 0) return [];
        var known = db.PlaylistSourceSnapshots.Where(item => linkIds.Contains(item.PlaylistLinkId) && item.SourceKind != null);
        var newest = known.GroupBy(item => item.PlaylistLinkId)
            .Select(group => new { PlaylistLinkId = group.Key, SnapshotVersion = group.Max(item => item.SnapshotVersion) });
        var rows = await known.AsNoTracking()
            .Join(newest, item => new { item.PlaylistLinkId, item.SnapshotVersion }, latest => latest, (item, _) => new { item.PlaylistLinkId, item.SourceKind })
            .ToArrayAsync(cancellationToken);
        var kinds = rows.ToDictionary(item => item.PlaylistLinkId, item => item.SourceKind!);
        var unknown = linkIds.Where(id => !kinds.ContainsKey(id)).ToArray();
        if (unknown.Length == 0) return kinds;
        foreach (var link in await db.PlaylistLinks.AsNoTracking().Where(item => unknown.Contains(item.Id))
                     .Select(item => new { item.Id, item.SourceProviderId, item.SourcePlaylistId }).ToArrayAsync(cancellationToken))
            if (Classify(link.SourceProviderId, link.SourcePlaylistId, null, null) is { } kind) kinds[link.Id] = kind;
        return kinds;
    }
}
