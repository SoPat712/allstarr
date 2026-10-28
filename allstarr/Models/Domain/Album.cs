namespace allstarr.Models.Domain;

/// <summary>
/// Represents an album
/// </summary>
public class Album
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? ArtistId { get; set; }
    public List<Artist> Artists { get; set; } = [];
    public bool? IsExplicit { get; set; }
    public string? Upc { get; set; }
    public string? Edition { get; set; }
    public string? Label { get; set; }
    public string? Notes { get; set; }
    public Dictionary<string, string> ProviderIds { get; set; } = [];
    public string? LastFmUrl { get; set; }
    public string? ReleaseDate { get; set; }
    public int? Year { get; set; }
    public int? SongCount { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? Genre { get; set; }
    public bool IsLocal { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
    public List<Song> Songs { get; set; } = new();
}
