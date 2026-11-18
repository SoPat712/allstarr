using System.Collections.Concurrent;

namespace allstarr.Services.Jellyfin;

public partial class JellyfinSessionManager
{
    private readonly ConcurrentDictionary<(string BackendInstanceId, string BackendPrincipalId, string ItemId), (bool Favorite, DateTimeOffset At)> _favoriteChanges = new();

    // Jellyfin saves a finished play with the favorite flag it read when the song started, so a heart given while the
    // song plays is lost when playback stops. Hearts given through Allstarr are noted so the stop can send them again.
    internal void NoteFavoriteChange(string backendInstanceId, string backendPrincipalId, string itemId, bool favorite)
    {
        if (_favoriteChanges.Count >= 1024) _favoriteChanges.Clear();
        _favoriteChanges[(backendInstanceId, backendPrincipalId, itemId)] = (favorite, _observationClock.GetUtcNow());
    }

    // The favorite change to send again after this session's playback of the item stops, when it was made during that playback.
    internal bool? TakeFavoriteChangeDuringPlayback(JellyfinSessionKey key, string itemId)
    {
        if (!_favoriteChanges.TryRemove((key.BackendInstanceId, key.BackendPrincipalId, itemId), out var change)) return null;
        if (!_sessions.TryGetValue(key, out var session)) return null;
        lock (session.SyncRoot)
        {
            var playing = session.HomeObservation?.ItemId ?? session.LastPlayingItemId;
            return string.Equals(playing, itemId, StringComparison.OrdinalIgnoreCase) &&
                session.PlaybackStartedAt is { } started && change.At >= started ? change.Favorite : null;
        }
    }
}
