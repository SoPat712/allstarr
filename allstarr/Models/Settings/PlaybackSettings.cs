namespace allstarr.Models.Settings;

public sealed class PlaybackSettings
{
    public bool PrepareUpcomingSongs { get; set; }
    public int PrepareUpcomingCount { get; set; } = 1;
    public bool LastFmSimilarEnabled { get; set; } = true;
    public bool ListenBrainzSimilarEnabled { get; set; } = true;
}
