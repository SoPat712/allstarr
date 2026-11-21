using System.Text.Json;
using allstarr.Core.Enrichment;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;

namespace allstarr.Core.ClientApi;

/// <summary>
/// What an addition is doing, in the steps a listener would name, read from the run's stored plan. An app shows it as
/// a notice, so the words say what happened to the song and what can be done, not which part of the server stopped.
/// </summary>
internal static class ClientLibraryAdditionProgress
{
    // The order a song goes through them. The scan is the media server's, so it ends when the item shows up there.
    private static readonly string[] Order = ["fetching", "checking", "lyrics", "tagging", "loudness", "placing", "scanning"];

    internal static ClientLibraryAddition Describe(ClientLibraryAddition value, PromotionRunRecord run)
    {
        if (Plan(run) is not { Tracks.Count: > 0 } album) return Single(value, run);
        var tracks = album.Tracks;
        var several = tracks.Count > 1;
        var steps = Order.Where(step => step != "loudness" || album.Profile.ReplayGain).ToArray();
        var first = tracks[0].Song;
        var held = tracks.Where(track => track.Outcome == "Needs review").ToArray();
        var added = tracks.Count(track => track.Outcome == "Placed" || track.Outcome == "Skipped" && track.Steps.Any(step => step.Name == "Dedupe"));
        var step = value.State switch
        {
            "adding" => Step(run.Step) ?? "fetching",
            "done" => "scanning",
            _ => null
        };
        var heldAt = value.State != "needsLook" ? null : Step(held.FirstOrDefault()?.Step ?? run.Step);
        // A cancelled or declined addition is over; how far it got is of no use to a notice.
        var open = value.State is "adding" or "needsLook" or "done";
        return value with
        {
            Title = Text(value.Unit == "album" ? first.Album : first.Title),
            Artist = Text(value.Unit == "album" && Text(first.AlbumArtist) != null ? first.AlbumArtist : first.Artist),
            Step = step,
            HeldAt = heldAt,
            StepsDone = open ? tracks.Sum(track => Done(track, album.Profile.ReplayGain, steps.Length - 1)) : null,
            StepsTotal = open ? steps.Length * tracks.Count : null,
            ItemsDone = open && several ? added : null,
            ItemsTotal = open && several ? tracks.Count : null,
            Reason = value.State switch
            {
                "adding" => Doing(step!, several),
                "done" => "Added; waiting for the library to show it.",
                "needsLook" when several => $"{added} of {tracks.Count} songs were added; {(held.Length == 0 ? "the rest stopped on an error" : held.Length == 1 ? "1 needs a look" : $"{held.Length} need a look")}. An administrator can decide in the dashboard.",
                "needsLook" => Held(heldAt, held.FirstOrDefault()),
                _ => value.Reason
            }
        };
    }

    // A run shared with another listener comes without its plan.
    private static PromotionAlbumPlan? Plan(PromotionRunRecord run)
    {
        try { return string.IsNullOrEmpty(run.PlanJson) ? null : LibraryPromotionService.ReadAlbum(run); }
        catch (JsonException) { return null; }
    }

    /// <summary>The media server shows the added item, so nothing is left to do.</summary>
    internal static ClientLibraryAddition Shown(ClientLibraryAddition value) => value.Step == null && value.StepsTotal == null ? value : value with
    {
        Step = null,
        StepsDone = value.StepsTotal,
        Reason = "Added to the library."
    };

    // An addition of one kept file, which has no per-song plan: it says where it is, not how far along.
    private static ClientLibraryAddition Single(ClientLibraryAddition value, PromotionRunRecord run)
    {
        ManagedTrackPathValues? track = null;
        try { track = string.IsNullOrEmpty(run.PlanJson) ? null : JsonSerializer.Deserialize<PromotionPlan>(run.PlanJson)?.Track; }
        catch (JsonException) { }
        var step = value.State == "adding" ? Step(run.Step) : value.State == "done" ? "scanning" : null;
        var heldAt = value.State == "needsLook" ? Step(run.Step) : null;
        return value with
        {
            Title = Text(track?.Title),
            Artist = Text(track?.Artist),
            Step = step,
            HeldAt = heldAt,
            Reason = value.State switch
            {
                "adding" when step != null => Doing(step, false),
                "done" => "Added; waiting for the library to show it.",
                "needsLook" => Held(heldAt, null),
                _ => value.Reason
            }
        };
    }

    private static int Done(PromotionTrack track, bool loudness, int beforeScan)
    {
        if (track.Outcome is "Placed" or "Skipped") return beforeScan;
        bool Has(string name) => track.Steps.Any(step => step.Name == name && step.Outcome != "Needs review" && step.Outcome != "Running");
        return new[] { Has("Acquire"), Has("Ceiling"), Has("Lyrics"), Has("Tags"), loudness && Has("ReplayGain") }.Count(done => done);
    }

    private static string? Step(string? name) => name switch
    {
        "Acquire" or "Copy" or "Dedupe" => "fetching",
        "Quality" or "Ceiling" => "checking",
        "Lyrics" => "lyrics",
        "Tags" => "tagging",
        "ReplayGain" => "loudness",
        "Placement" => "placing",
        "Refresh" => "scanning",
        _ => null
    };

    private static string Doing(string step, bool several) => step switch
    {
        "fetching" => several ? "Fetching the songs." : "Fetching the song.",
        "checking" => "Checking the audio quality.",
        "lyrics" => "Looking for lyrics.",
        "tagging" => "Writing tags and cover art.",
        "loudness" => "Measuring loudness.",
        "placing" => several ? "Putting the files in the library." : "Putting the file in the library.",
        _ => "Waiting for the library to show it."
    };

    private static string Held(string? heldAt, PromotionTrack? track) => (heldAt switch
    {
        "fetching" => "None of your services could deliver this song at the quality your library asks for, so it was not added.",
        "checking" => track?.QualityCheck?.Verdict switch
        {
            "lossy-transcode-suspected" => "This file looks like it was made from a lossy copy, so it was not added.",
            "upsampled" => "This file looks upsampled from a lower sample rate, so it was not added.",
            "padded-bit-depth" => "This file holds less bit depth than it claims, so it was not added.",
            _ => "The audio quality check could not confirm this file, so it was not added."
        },
        null => "Adding stopped on an error, so nothing was added.",
        _ => "This song's file could not be prepared, so it was not added."
    }) + " An administrator can decide in the dashboard.";

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
