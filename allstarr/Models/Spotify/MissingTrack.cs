namespace allstarr.Models.Spotify;

public class MissingTrack
{
    public string SpotifyId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public List<string> Artists { get; set; } = new();
    
    public string PrimaryArtist => Artists.FirstOrDefault() ?? "";
    public string AllArtists => string.Join(", ", Artists);
}
