using allstarr.Models.Domain;
using allstarr.Models.Spotify;

namespace allstarr.Services.Spotify;

public static class LegacyPlaylistMatchRecovery
{
    public static List<MatchedTrack> ReconstructExact(
        IReadOnlyCollection<SpotifyPlaylistTrack> sourceTracks,
        IReadOnlyCollection<Song> legacySongs)
    {
        var claimedSpotifyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recovered = new List<MatchedTrack>();

        foreach (var song in legacySongs.Where(ExternalTrackPlaybackPolicy.CanUseForPlayback))
        {
            var candidates = sourceTracks.Where(track =>
                    !claimedSpotifyIds.Contains(track.SpotifyId) &&
                    Same(track.Title, song.Title) &&
                    track.Artists.Any(artist => Same(artist, song.Artist)))
                .Take(2)
                .ToList();
            if (candidates.Count != 1)
                continue;

            var track = candidates[0];
            claimedSpotifyIds.Add(track.SpotifyId);
            recovered.Add(new MatchedTrack
            {
                Position = track.Position,
                SpotifyId = track.SpotifyId,
                SpotifyTitle = track.Title,
                SpotifyArtist = track.PrimaryArtist,
                Isrc = track.Isrc,
                MatchType = "legacy-exact-identity",
                MatchedSong = song
            });
        }

        return recovered.OrderBy(match => match.Position).ToList();
    }

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
