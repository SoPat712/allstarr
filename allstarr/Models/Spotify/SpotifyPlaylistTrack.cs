using allstarr.Models.Domain;

namespace allstarr.Models.Spotify;

/// <summary>
/// Represents a track from a Spotify playlist with full metadata including position.
/// This model preserves track ordering which is critical for playlists like Release Radar.
/// </summary>
public class SpotifyPlaylistTrack
{
    /// <summary>
    /// Spotify track ID (e.g., "3a8mo25v74BMUOJ1IDUEBL")
    /// </summary>
    public string SpotifyId { get; set; } = string.Empty;

    /// <summary>
    /// Track's position in the playlist (0-based index).
    /// This is critical for maintaining correct playlist order.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Track title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Album name
    /// </summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>
    /// Album Spotify ID
    /// </summary>
    public string AlbumId { get; set; } = string.Empty;

    /// <summary>
    /// List of artist names
    /// </summary>
    public List<string> Artists { get; set; } = new();

    /// <summary>
    /// List of artist Spotify IDs
    /// </summary>
    public List<string> ArtistIds { get; set; } = new();

    /// <summary>
    /// ISRC (International Standard Recording Code) for exact track identification.
    /// This enables precise matching across different streaming services.
    /// </summary>
    public string? Isrc { get; set; }

    /// <summary>
    /// Track duration in milliseconds
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// Whether the track contains explicit content
    /// </summary>
    public bool Explicit { get; set; }

    /// <summary>
    /// Track's popularity score (0-100)
    /// </summary>
    public int Popularity { get; set; }

    /// <summary>
    /// Preview URL for 30-second audio clip (may be null)
    /// </summary>
    public string? PreviewUrl { get; set; }

    /// <summary>
    /// Album artwork URL (largest available)
    /// </summary>
    public string? AlbumArtUrl { get; set; }

    /// <summary>
    /// Release date of the album (format varies: YYYY, YYYY-MM, or YYYY-MM-DD)
    /// </summary>
    public string? ReleaseDate { get; set; }

    /// <summary>
    /// When this track was added to the playlist
    /// </summary>
    public DateTime? AddedAt { get; set; }

    /// <summary>
    /// Disc number within the album
    /// </summary>
    public int DiscNumber { get; set; } = 1;

    /// <summary>
    /// Track number within the disc
    /// </summary>
    public int TrackNumber { get; set; } = 1;

    /// <summary>
    /// Primary (first) artist name
    /// </summary>
    public string PrimaryArtist => Artists.FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// All artists as a comma-separated string
    /// </summary>
    public string AllArtists => string.Join(", ", Artists);

    /// <summary>
    /// Track duration as TimeSpan
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

    /// <summary>
    /// Converts to the legacy MissingTrack format for compatibility with existing matching logic.
    /// </summary>
    public MissingTrack ToMissingTrack() => new()
    {
        SpotifyId = SpotifyId,
        Title = Title,
        Album = Album,
        Artists = Artists
    };
}

/// <summary>
/// Represents a Spotify playlist with its tracks in order.
/// </summary>
public class SpotifyPlaylist
{
    /// <summary>
    /// Spotify playlist ID
    /// </summary>
    public string SpotifyId { get; set; } = string.Empty;

    /// <summary>
    /// Playlist name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Playlist description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Playlist owner's display name
    /// </summary>
    public string? OwnerName { get; set; }

    /// <summary>
    /// Playlist owner's Spotify ID
    /// </summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// Total number of tracks in the playlist
    /// </summary>
    public int TotalTracks { get; set; }

    /// <summary>
    /// Playlist cover image URL
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Whether this is a collaborative playlist
    /// </summary>
    public bool Collaborative { get; set; }

    /// <summary>
    /// Whether this playlist is public
    /// </summary>
    public bool Public { get; set; }

    /// <summary>
    /// Tracks in the playlist, ordered by position
    /// </summary>
    public List<SpotifyPlaylistTrack> Tracks { get; set; } = new();

    /// <summary>
    /// When this data was fetched from Spotify
    /// </summary>
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Snapshot ID for change detection (Spotify's playlist version identifier)
    /// </summary>
    public string? SnapshotId { get; set; }

    /// <summary>
    /// Playlist creation date when provided by Spotify.
    /// If unavailable, this may be inferred from track AddedAt timestamps.
    /// </summary>
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// Represents a Spotify track that has been matched to an external provider track.
/// Preserves position for correct playlist ordering.
/// </summary>
public class MatchedTrack
{
    /// <summary>
    /// Position in the original Spotify playlist (0-based)
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Original Spotify track ID
    /// </summary>
    public string SpotifyId { get; set; } = string.Empty;

    /// <summary>
    /// Original Spotify track title (for debugging/logging)
    /// </summary>
    public string SpotifyTitle { get; set; } = string.Empty;

    /// <summary>
    /// Original Spotify artist (for debugging/logging)
    /// </summary>
    public string SpotifyArtist { get; set; } = string.Empty;

    /// <summary>
    /// ISRC used for matching (if available)
    /// </summary>
    public string? Isrc { get; set; }

    /// <summary>
    /// How the match was made: "isrc" or "fuzzy"
    /// </summary>
    public string MatchType { get; set; } = string.Empty;

    /// <summary>
    /// The matched song from the external provider
    /// </summary>
    public Song MatchedSong { get; set; } = null!;
}
