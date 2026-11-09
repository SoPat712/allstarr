namespace allstarr.Core.Enrichment;

public sealed partial class LibraryPromotionService
{
    private static PromotionProfileStep[] ProfileSteps(PromotionProfile profile) =>
    [
        new("quality", "Check the copy is really lossless", profile.FakeLosslessCheck ? "Spectrum check for upscaled files" : "Spectrum check disabled in your profile", profile.FakeLosslessCheck),
        new("tags-cover", !profile.MusicBrainzTags && profile.CoverMode != "none" ? "Cover art" : "MusicBrainz tags and cover art",
            (profile.MusicBrainzTags ? "Release, recording and artist IDs" : "MusicBrainz tags disabled") + " · " + (profile.CoverMode switch
            {
                "both" => "cover.jpg + embedded",
                "embedded" => "embedded cover",
                "cover.jpg" => "cover.jpg",
                _ => "cover art disabled"
            }), profile.MusicBrainzTags || profile.CoverMode != "none"),
        new("lyrics", "Synced lyrics", profile.LyricsMode switch
            {
                "both" => "Best-ranked version, only if its timing fits this file · .lrc + embedded",
                ".lrc" => "Best-ranked version, only if its timing fits this file · .lrc next to the song",
                "embedded" => "Best-ranked version, only if its timing fits this file · embedded",
                _ => "Lyrics disabled in your profile"
            }, profile.LyricsMode != "off"),
        new("replaygain", "ReplayGain", profile.ReplayGain ? "Track and album gain · partial additions measure the ready songs together" : "ReplayGain disabled in your profile", profile.ReplayGain),
        new("handover", "Switch to the library copy", "Playlists, mixes, hearts and plays move to the new file." +
            (profile.RemoveKeptCopy ? " Release the kept copy after confirmation." : " Retain the kept copy."), true)
    ];
}
