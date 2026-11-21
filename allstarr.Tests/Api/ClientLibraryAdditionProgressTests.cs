using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Enrichment;
using allstarr.Core.Storage;
using allstarr.Models.Domain;

namespace allstarr.Tests.Api;

public sealed class ClientLibraryAdditionProgressTests
{
    [Fact]
    public void ASongBeingAddedSaysWhichStepItIsAtAndHowFarAlong()
    {
        var track = Track("Glown Up", "GIRLSET", ("Acquire", "Complete"), ("Quality", "Complete"), ("Ceiling", "Complete"), ("Lyrics", "None fits"));
        var addition = ClientLibraryAdditions.From(Run("Running", "Tags", "song", track));
        Assert.Equal("adding", addition.State);
        Assert.Equal("tagging", addition.Step);
        Assert.Equal((3, 7), (addition.StepsDone, addition.StepsTotal));
        Assert.Equal(("Glown Up", "GIRLSET"), (addition.Title, addition.Artist));
        Assert.Equal("Writing tags and cover art.", addition.Reason);
        Assert.Null(addition.HeldAt);
        Assert.Null(addition.ItemsTotal);
    }

    [Fact]
    public void ASongJustAskedForIsBeingFetched()
    {
        var addition = ClientLibraryAdditions.From(Run("Queued", "Acquire", "song", Track("Glown Up", "GIRLSET")));
        Assert.Equal(("adding", "fetching", 0), (addition.State, addition.Step, addition.StepsDone));
    }

    [Theory]
    [InlineData("unknown", "The audio quality check could not confirm this file, so it was not added. An administrator can decide in the dashboard.")]
    [InlineData("upsampled", "This file looks upsampled from a lower sample rate, so it was not added. An administrator can decide in the dashboard.")]
    public void AHeldSongSaysWhereItStoppedAndThatNothingWasAdded(string verdict, string reason)
    {
        var track = Track("Glown Up", "GIRLSET", ("Acquire", "Complete"), ("Quality", "Needs review"));
        track.Outcome = "Needs review";
        track.Step = "Quality";
        track.QualityCheck = new(48000, 24, 22000, verdict, 0.3);
        var addition = ClientLibraryAdditions.From(Run("Needs review", "Quality", "song", track));
        Assert.Equal("needsLook", addition.State);
        Assert.Equal("checking", addition.HeldAt);
        Assert.Null(addition.Step);
        Assert.Equal(reason, addition.Reason);
        Assert.Equal((1, 7), (addition.StepsDone, addition.StepsTotal));
    }

    [Fact]
    public void AnAlbumCountsItsSongsAndNamesItself()
    {
        var placed = Track("One", "Singer", ("Acquire", "Complete"), ("Ceiling", "Complete"), ("Lyrics", "Complete"), ("Tags", "Complete"), ("ReplayGain", "Complete"), ("Placement", "Complete"));
        placed.Outcome = "Placed";
        var there = Track("Two", "Singer", ("Dedupe", "Skipped"));
        there.Outcome = "Skipped";
        var held = Track("Three", "Singer", ("Acquire", "Needs review"));
        held.Outcome = "Needs review";
        var addition = ClientLibraryAdditions.From(Run("Needs review", "Acquire", "album", placed, there, held));
        Assert.Equal(("The Album", "The Band"), (addition.Title, addition.Artist));
        Assert.Equal((2, 3), (addition.ItemsDone, addition.ItemsTotal));
        Assert.Equal((12, 21), (addition.StepsDone, addition.StepsTotal));
        Assert.Equal("fetching", addition.HeldAt);
        Assert.Equal("2 of 3 songs were added; 1 needs a look. An administrator can decide in the dashboard.", addition.Reason);
    }

    [Fact]
    public void AnAddedSongIsStillBeingScannedUntilTheLibraryShowsIt()
    {
        var track = Track("Glown Up", "GIRLSET", ("Acquire", "Complete"), ("Ceiling", "Complete"), ("Lyrics", "Complete"), ("Tags", "Complete"), ("ReplayGain", "Complete"), ("Placement", "Complete"));
        track.Outcome = "Placed";
        var placed = ClientLibraryAdditions.From(Run("Placed", "Refresh", "song", track));
        Assert.Equal(("done", "scanning"), (placed.State, placed.Step));
        Assert.Equal((6, 7), (placed.StepsDone, placed.StepsTotal));

        var shown = ClientLibraryAdditionProgress.Shown(placed with { LibraryItemId = "item" });
        Assert.Null(shown.Step);
        Assert.Equal((7, 7), (shown.StepsDone, shown.StepsTotal));
        Assert.Equal("Added to the library.", shown.Reason);

        // Days later the library still does not show it: it was moved or removed there, and the addition is not waiting.
        var run = Run("Placed", "Refresh", "song", track);
        run.CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var old = ClientLibraryAdditions.From(run);
        Assert.Equal(("done", "Added to the library."), (old.State, old.Reason));
        Assert.Null(old.Step);
        Assert.Null(old.StepsTotal);
    }

    [Fact]
    public void ACancelledAdditionKeepsItsNameAndDropsItsProgress()
    {
        var addition = ClientLibraryAdditions.From(Run("Cancelled", "Lyrics", "song", Track("Glown Up", "GIRLSET", ("Acquire", "Complete"))));
        Assert.Equal(("cancelled", "Glown Up"), (addition.State, addition.Title));
        Assert.Null(addition.Step);
        Assert.Null(addition.StepsTotal);
    }

    [Fact]
    public void AProfileWithoutLoudnessHasOneStepFewer()
    {
        var run = Run("Running", "Tags", "song", Track("Glown Up", "GIRLSET", ("Acquire", "Complete")));
        run.PlanJson = JsonSerializer.Serialize(new PromotionAlbumPlan(new() { ReplayGain = false }, LibraryPromotionService.ReadAlbum(run)!.Tracks));
        Assert.Equal(6, ClientLibraryAdditions.From(run).StepsTotal);
    }

    private static PromotionTrack Track(string title, string artist, params (string Name, string Outcome)[] steps) => new()
    {
        RecordingId = Guid.NewGuid(),
        Song = new Song { Title = title, Artist = artist, Album = "The Album", AlbumArtist = "The Band" },
        Steps = steps.Select(step => new PromotionTrackStep(step.Name, step.Outcome, "")).ToList()
    };

    private static PromotionRunRecord Run(string outcome, string step, string unit, params PromotionTrack[] tracks) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        Outcome = outcome,
        Step = step,
        RequestJson = JsonSerializer.Serialize(new PromoteSongRequest(tracks[0].RecordingId, Guid.Empty, Guid.NewGuid(), "library", "key") { Unit = unit }),
        PlanJson = JsonSerializer.Serialize(new PromotionAlbumPlan(new(), tracks.ToList()))
    };
}
