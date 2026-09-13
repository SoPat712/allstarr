namespace allstarr.Models.Domain;

public sealed class Song
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? ArtistId { get; set; }

    public List<string> Artists { get; set; } = [];

    // Index-matched with Artists.
    public List<string> ArtistIds { get; set; } = [];

    public string Album { get; set; } = string.Empty;
    public string? AlbumId { get; set; }
    public int? Duration { get; set; } // In seconds
    public int? Bitrate { get; set; } // In bits per second
    public int? Track { get; set; }
    public int? DiscNumber { get; set; }
    public int? TotalTracks { get; set; }
    public int? Year { get; set; }
    public string? Genre { get; set; }
    public string? CoverArtUrl { get; set; }

    public string? CoverArtUrlLarge { get; set; }
    public int? Bpm { get; set; }
    public string? Isrc { get; set; }
    public string? SpotifyId { get; set; }
    public string? ReleaseDate { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Composer { get; set; }
    public string? Label { get; set; }
    public string? Copyright { get; set; }
    public List<string> Contributors { get; set; } = [];
    public bool IsLocal { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
    public string? LocalPath { get; set; }

    // Deezer: 0 clean, 1 explicit, 2 unknown, 3 edited, 6/7 no advice.
    public int? ExplicitContentLyrics { get; set; }

    // Preserves the complete local Jellyfin object across cache round-trips.
    public Dictionary<string, object?>? JellyfinMetadata { get; set; }
}
