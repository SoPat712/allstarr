namespace allstarr.Models.Settings;

public class QobuzSettings
{
    public string? UserAuthToken { get; set; }
    public string? UserId { get; set; }
    public string? Quality { get; set; } = "FLAC_24_HIGH";
    public int MinRequestIntervalMs { get; set; } = 200;
}
