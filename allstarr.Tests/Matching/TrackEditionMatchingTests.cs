using allstarr.Core.Matching;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class TrackEditionMatchingTests
{
    [Theory]
    [InlineData("Say You Will (Album Version)", "Say You Will", "Kanye West", "Kanye West")]
    [InlineData("Welcome To Heartbreak (Album Version)", "Welcome To Heartbreak", "Kanye West", "Kanye West, Kid Cudi")]
    [InlineData("Come Together - Remastered 2009", "Come Together", "The Beatles", "The Beatles")]
    [InlineData("Come Together (2019 Remaster)", "Come Together", "The Beatles", "The Beatles")]
    [InlineData("Gold Digger (Album Version (Explicit))", "Gold Digger (Explicit)", "Kanye West", "Kanye West, Jamie Foxx")]
    [InlineData("Diamonds From Sierra Leone (Remix - Album Version (Explicit))", "Diamonds From Sierra Leone - Remix (Explicit)", "Kanye West", "Kanye West")]
    [InlineData("Song (LP Version)", "Song", "Artist", "Artist")]
    [InlineData("Song (Original Version)", "Song", "Artist", "Artist")]
    [InlineData("Song (Digitally Remastered 2011)", "Song", "Artist", "Artist")]
    [InlineData("Song - Digital Remaster Version", "Song", "Artist", "Artist")]
    [InlineData("Song [Re-Mastered 2011]", "Song", "Artist", "Artist")]
    [InlineData("Song (Expanded Edition)", "Song", "Artist", "Artist")]
    [InlineData("Song (Deluxe Version)", "Song", "Artist", "Artist")]
    [InlineData("Song (feat. Guest, Other)", "Song", "Artist", "Artist, Guest, Other")]
    [InlineData("Song feat. Guest", "Song", "Artist", "Artist, Guest")]
    [InlineData("No Church In The Wild", "No Church In The Wild", "JAY-Z", "JAŸ-Z, Kanye West")]
    [InlineData("Otis", "Otis", "Kanye West, JAY-Z", "JAY-Z")]
    public void EditionLabelsMatchTheOwnedRecording(string title, string libraryTitle, string artist, string libraryArtist)
    {
        Assert.Equal(FuzzyMatcher.NormalizeTitleForMatching(libraryTitle, libraryArtist, artist),
            FuzzyMatcher.NormalizeTitleForMatching(title, artist, libraryArtist));
        Assert.True(FuzzyMatcher.TitleAnnotationsMatch(title, artist, libraryTitle, libraryArtist));
        var source = Source(title, artist);
        var candidate = Candidate(libraryTitle, libraryArtist);
        var decision = new TrackMatchDecisionEngine().Decide(Scope(), source, [candidate]);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(candidate.LibraryTrackId, decision.SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Acoustic")]
    [InlineData("Remix")]
    [InlineData("Piano Cover")]
    [InlineData("Slowed + Reverb")]
    [InlineData("Clean")]
    [InlineData("Explicit")]
    [InlineData("Mono")]
    [InlineData("Stereo")]
    public void NestedEditionLabelsKeepDifferentRecordingsSeparate(string version)
    {
        var candidate = Candidate($"Song (Album Version ({version}))", "Artist");
        var decision = new TrackMatchDecisionEngine().Decide(Scope(), Source("Song", "Artist"), [candidate]);
        Assert.NotEqual(TrackMatchReviewState.Accepted, decision.State);
        Assert.True(decision.RequiresReview);
    }

    [Theory]
    [InlineData("Song (LP Version Alternate Take)")]
    [InlineData("Song - Original Version Alternate Take")]
    [InlineData("Song (Digitally Remastered 2011 Alternate Take)")]
    [InlineData("Song [Re-Mastered 2011 Additional Vocals]")]
    public void AdditionalEditionLabelsDoNotHideUnknownQualifiers(string title)
    {
        var decision = new TrackMatchDecisionEngine().Decide(Scope(), Source("Song", "Artist"), [Candidate(title, "Artist")]);
        Assert.NotEqual(TrackMatchReviewState.Accepted, decision.State);
        Assert.True(decision.RequiresReview);
    }

    [Fact]
    public void EditionNormalizationKeepsManualPinsAndRejections()
    {
        var scope = Scope();
        var source = Source("Song (LP Version)", "Artist");
        var owned = Candidate("Song", "Artist");
        var pinned = Candidate("Song", "Artist") with { BackendItemId = "chosen" };
        var engine = new TrackMatchDecisionEngine();
        var pin = engine.Decide(scope, source, [owned, pinned],
            new(scope.UserId, source.ProviderId, source.ExternalId, pinned.LibraryTrackId));
        Assert.Equal(TrackMatchReviewState.Pinned, pin.State);
        Assert.Equal(pinned.LibraryTrackId, pin.SelectedLibraryTrackId);
        var rejected = engine.Decide(scope, source, [owned],
            new(scope.UserId, source.ProviderId, source.ExternalId, null, new HashSet<Guid> { owned.LibraryTrackId }));
        Assert.Equal(TrackMatchReviewState.Rejected, rejected.State);
        Assert.Null(rejected.SelectedLibraryTrackId);
    }

    private static TrackMatchScope Scope() => new(Guid.NewGuid(), "backend", null, 1, 1, new HashSet<string> { "music" });
    private static ExternalTrackMatchSnapshot Source(string title, string artist) =>
        new("snapshot", "deezer", "42", title, artist, "Album", null, 200000, null, null, null);
    private static LocalTrackMatchCandidate Candidate(string title, string artist) =>
        new(Guid.NewGuid(), null, "backend", "music", "owned", null, title, artist, "Album", null, 200000, null, null, null);
}
