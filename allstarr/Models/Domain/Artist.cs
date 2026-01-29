namespace allstarr.Models.Domain;

/// <summary>
/// Represents an artist
/// </summary>
public class Artist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public int? AlbumCount { get; set; }
    public bool IsLocal { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
}
