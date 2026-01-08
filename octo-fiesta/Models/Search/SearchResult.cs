namespace octo_fiesta.Models.Search;

using octo_fiesta.Models.Domain;

/// <summary>
/// Search result combining local and external results
/// </summary>
public class SearchResult
{
    public List<Song> Songs { get; set; } = new();
    public List<Album> Albums { get; set; } = new();
    public List<Artist> Artists { get; set; } = new();
}
