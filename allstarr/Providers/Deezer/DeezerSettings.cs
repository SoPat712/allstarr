namespace allstarr.Models.Settings;

public class DeezerSettings
{
    public string? Arl { get; set; }
    public string? ArlFallback { get; set; }
    public string? Quality { get; set; } = "FLAC";
    public int MinRequestIntervalMs { get; set; } = 200;
}
