using allstarr.Core.Matching;

namespace allstarr.Tests;

public sealed class TrackMatchLibraryFirstTests
{
    public static TheoryData<string, string, string?, long, string, string, string?, long> LibraryCopies => new()
    {
        { "Jackie Chan (feat. Preme & Post Malone)", "Tiësto & Dzeko", "Jackie Chan (feat. Preme & Post Malone) - Single", 215_760,
          "Jackie Chan", "Tiësto, Dzeko, Preme, Post Malone", "Jackie Chan", 215_759 },
        { "Dance Now", "JID & Kenny Mason", "The Forever Story", 227_556,
          "Dance Now", "JID, Kenny Mason", "The Forever Story (Extended Version)", 227_555 },
        { "חרבו דרבו", "Ness & Stilla", null, 150_375, "חרבו דרבו", "סטילה, נס", null, 150_374 },
        { "תיק קטן", "Ness & Stilla", null, 173_120, "תיק קטן", "סטילה, נס", null, 173_119 },
        { "See U Tonight", "Kylie Cantrall & ILLIT", null, 181_400,
          "See U Tonight (feat. YUNAH & MINJU of ILLIT)", "Kylie Cantrall, ILLIT", null, 181_400 },
        { "FUFN (F**k You For Now)", "JADE", null, 196_300, "FUFN (Fuck You for Now)", "JADE", null, 196_000 },
        { "Edge of Great (feat. Madison Reyes, Charlie Gillespie, Owen Joyner & Jeremy Shada)", "Julie and the Phantoms Cast", null, 213_200,
          "Edge of Great", "Madison Reyes, Charlie Gillespie, Owen Joyner, Jeremy Shada", null, 213_100 },
        { "Scared of Love (with instrumental by Gnealz)", "Juice WRLD", null, 209_800, "Scared of Love", "Juice WRLD", null, 209_700 },
        { "Mona Lisa - Spider-Man: Across the Spider-Verse", "Dominic Fike", null, 205_900, "Mona Lisa", "Dominic Fike", "Sunburn", 205_800 },
        { "Costa Rica (feat. Guapdad 4000, Reese LAFLARE, Jace, Mez, Smokepurpp, Buddy & Ski Mask the Slump God)", "Dreamville, Bas & JID", null, 217_400,
          "Costa Rica", "Dreamville, Bas, JID, Guapdad 4000, Reese LAFLARE, Jace, Mez, Smokepurpp, Buddy, Ski Mask the Slump God", null, 217_300 },
        { "Sunroof", "Nicky Youre & hey daisy", "Sunroof", 163_025, "Sunroof", "Nicky Youre, dazy", "Sunroof", 163_025 },
        { "Sucker for Pain (feat. Logic, Ty Dolla $ign & X Ambassadors)", "Lil Wayne, Wiz Khalifa & Imagine Dragons", null, 243_400,
          "Sucker for Pain", "Lil Wayne, Wiz Khalifa, Imagine Dragons, Logic, Ty Dolla $ign, X Ambassadors", null, 244_200 },
        { "Surround Sound (feat. 21 Savage & Baby Tate)", "JID", null, 252_600, "Surround Sound", "JID, 21 Savage, Baby Tate", null, 252_500 },
        { "Stay Alive (Prod. SUGA of BTS)", "Jung Kook", null, 205_100, "Stay Alive", "전정국", null, 205_000 },
        { "Wicked Game (feat. Tom Ellis)", "Lucifer Cast", null, 199_700, "Wicked Game", "Tom Ellis", null, 199_600 },
        { "Let Me Go (feat. Florida Georgia Line & watt)", "Hailee Steinfeld & Alesso", null, 174_800,
          "Let Me Go", "Hailee Steinfeld, Alesso, Florida Georgia Line, watt", null, 174_700 },
        { "death bed (coffee for your head) [feat. beabadoobee]", "Powfu & beabadoobee", null, 173_300,
          "death bed", "Powfu, beabadoobee", null, 173_200 },
        { "Kamikaze", "Lil Mosey", "Northsbest (Extended)", 139_400, "Kamakazi", "Lil Mosey", "Northsbest", 139_300 },
        { "Guinnesses (feat. Angelika & 4ize)", "MF DOOM", "MM..FOOD", 281_200, "Guinesses", "MF DOOM", "MM..FOOD", 281_100 },
        { "Ni**as in Paris", "JAŸ-Z & Kanye West", "Watch the Throne (Deluxe)", 219_300,
          "Niggas in Paris", "Jay‐Z, Kanye West", "Watch the Throne", 219_200 },
        // A guest one copy names under another alias must not undo the match the artists alone make.
        { "America's Most Blunted (feat. Quasimoto)", "Madvillain", "Madvillainy", 234_040,
          "America’s Most Blunted", "Madvillain, Lord Quas", "Madvillainy", 234_106 },
        { "Rover (feat. Lil Tecca)", "S1mba", "Rover (feat. Lil Tecca) - Single", 166_806, "Rover", "S1MBA, DTG", "Rover", 167_916 }
    };

    [Theory]
    [MemberData(nameof(LibraryCopies))]
    public void LibraryFileIsChosenOverStreamingCopies(string title, string artist, string? album, long duration,
        string libraryTitle, string libraryArtist, string? libraryAlbum, long libraryDuration)
    {
        var source = Source(title, artist, album, duration);
        var library = Library(libraryTitle, libraryArtist, libraryAlbum, libraryDuration);
        var streaming = Streaming(title, FirstCredit(artist), album, duration);
        var result = new TrackMatchDecisionEngine().Decide(Scope(), source, [streaming, library]);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(library.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Equal(1, result.Candidates.Single(item => item.LibraryTrackId == library.LibraryTrackId).Components!["acceptanceQualified"]);
    }

    [Fact]
    public void StoredJackieChanScoresQualifyTheLibraryFile()
    {
        var source = Source("Jackie Chan (feat. Preme & Post Malone)", "Tiësto & Dzeko", "Jackie Chan (feat. Preme & Post Malone) - Single", 215_760);
        var library = Library("Jackie Chan", "Tiësto, Dzeko, Preme, Post Malone", "Jackie Chan", 215_759);
        var streaming = Streaming("Jackie Chan", "Tiësto", "Jackie Chan", 215_760);
        var result = new TrackMatchDecisionEngine().Decide(Scope(), source, [streaming, library]);
        var score = result.Candidates.Single(item => item.LibraryTrackId == library.LibraryTrackId);
        Assert.Equal(library.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Equal(1, score.Components!["artist"]);
        Assert.Equal(1, score.Components["versionTags"]);
    }

    [Fact]
    public void AlbumEditionDoesNotCountAsTheTracksVersion()
    {
        var source = Source("Dance Now", "JID & Kenny Mason", "The Forever Story", 227_556);
        var library = Library("Dance Now", "JID, Kenny Mason", "The Forever Story (Extended Version)", 227_555);
        var outcome = TrackMatchRuleSet.Evaluate(source, library, new TrackMatchPolicy());
        Assert.Equal(1, outcome.Components["versionTags"]);
        Assert.True(outcome.Confidence >= .88);
    }

    [Theory]
    [InlineData("Dip", "Tyga", null, 127_000L, "Dip", "Tyga, Nicki Minaj", null, 195_000L)]
    [InlineData("9 to 5", "Dolly Parton", null, 180_000L, "9 to 5", "Dolly Parton", null, 166_000L)]
    [InlineData("State Of Grace (Taylor's Version)", "Taylor Swift", "Red (Taylor's Version)", 295_000L,
        "State of Grace", "Taylor Swift", "Red", 295_000L)]
    public void DifferentRecordingsStayUnmatched(string title, string artist, string? album, long duration,
        string libraryTitle, string libraryArtist, string? libraryAlbum, long libraryDuration)
    {
        var source = Source(title, artist, album, duration);
        var library = Library(libraryTitle, libraryArtist, libraryAlbum, libraryDuration);
        var streaming = Streaming(title, artist, album, duration);
        var result = new TrackMatchDecisionEngine().Decide(Scope(), source, [streaming, library]);
        Assert.NotEqual(library.LibraryTrackId, result.SelectedLibraryTrackId);
        var alone = new TrackMatchDecisionEngine().Decide(Scope(), source, [library]);
        Assert.NotEqual(TrackMatchReviewState.Accepted, alone.State);
    }

    [Theory]
    [InlineData("Simon & Garfunkel", "Simon & Garfunkel", 1)]
    [InlineData("Earth, Wind & Fire", "Earth, Wind & Fire", 1)]
    [InlineData("JID & Kenny Mason", "JID, Kenny Mason", 1)]
    [InlineData("Tiësto & Dzeko, Preme, Post Malone", "Tiësto, Dzeko, Preme, Post Malone", 1)]
    public void ArtistsCompareAsSetsOfNames(string left, string right, double expected) =>
        Assert.Equal(expected, TrackMatchSignals.ArtistSimilarity(left, right));

    [Fact]
    public void ScriptAllowanceNeedsTheExactLength()
    {
        var source = Source("חרבו דרבו", "Ness & Stilla", null, 150_375);
        var library = Library("חרבו דרבו", "סטילה, נס", null, 153_375);
        var result = new TrackMatchDecisionEngine().Decide(Scope(), source, [library]);
        Assert.NotEqual(TrackMatchReviewState.Accepted, result.State);
    }

    private static string FirstCredit(string artist) => artist.Split([" & ", ", ", " and "], StringSplitOptions.None)[0];

    private static readonly Guid User = Guid.CreateVersion7();
    private static TrackMatchScope Scope() => new(User, "main", null, 1, 1, new HashSet<string> { "music" });

    private static ExternalTrackMatchSnapshot Source(string title, string artist, string? album, long duration) => new(
        "snapshot", "apple", "source", title, artist, album, null, duration, null, null, null);

    private static LocalTrackMatchCandidate Library(string title, string artist, string? album, long duration) => new(
        Guid.CreateVersion7(), User, "main", "music", Guid.CreateVersion7().ToString(), null,
        title, artist, album, null, duration, null, null, null);

    private static LocalTrackMatchCandidate Streaming(string title, string artist, string? album, long duration) => new(
        Guid.CreateVersion7(), User, "main", null, Guid.CreateVersion7().ToString(), null,
        title, artist, album, null, duration, null, null, null,
        new Dictionary<string, string> { ["deezer"] = Guid.CreateVersion7().ToString() }, IsLocal: false);
}
