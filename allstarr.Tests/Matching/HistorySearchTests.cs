using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class HistorySearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedAnswerSurvivesOtherProviderException(bool accepted)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["qobuz", "deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "qobuz", It.IsAny<string>(), 15))
            .ThrowsAsync(new HttpRequestException());
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([
                new() { ExternalProvider = "deezer", ExternalId = "other", Title = accepted ? "Song" : "Different", Artist = "Artist", Duration = 180 }]));
        var outcome = await Search(gateway, "Song", "Artist");
        Assert.True(outcome.IsSuccess);
        Assert.Single(outcome.RequireValue().ExternalCandidates);
        if (accepted) Assert.Equal(TrackMatchReviewState.Accepted, outcome.RequireValue().Decision.State);
    }

    [Theory]
    [InlineData("Going Bad (feat. Drake)", "Meek Mill", "Going Bad (feat. Drake)", "Meek Mill")]
    [InlineData("Feels (feat. Pharrell Williams, Katy Perry & Big Sean)", "Calvin Harris", "Feels", "Calvin Harris, Pharrell Williams, Katy Perry, Big Sean")]
    [InlineData("Hey Julie! (feat. Lil Yachty)", "KYLE", "Hey Julie!", "KYLE, Lil Yachty")]
    [InlineData("X", "Nicky Jam", "X", "Nicky Jam")]
    [InlineData("MIA (feat. Drake)", "Bad Bunny", "MIA", "Bad Bunny, Drake")]
    [InlineData("Eastside (with Halsey & Khalid)", "benny blanco", "Eastside", "benny blanco, Halsey, Khalid")]
    [InlineData("LOYAL (feat. Drake)", "PARTYNEXTDOOR", "LOYAL", "PARTYNEXTDOOR, Drake")]
    [InlineData("Old Town Road (feat. Billy Ray Cyrus) - Remix", "Lil Nas X", "Old Town Road (feat. Billy Ray Cyrus) [Remix]", "Lil Nas X")]
    [InlineData("Godzilla (feat. Juice WRLD)", "Eminem", "Godzilla (feat. Juice WRLD)", "Eminem")]
    [InlineData("Falling Down - Bonus Track", "Lil Peep", "Falling Down (Bonus Track)", "Lil Peep")]
    [InlineData("FRIENDS", "Marshmello", "FRIENDS", "Marshmello")]
    [InlineData("1999", "Charli xcx", "1999", "Charli XCX")]
    [InlineData("ROCKSTAR (feat. Roddy Ricch)", "DaBaby", "ROCKSTAR", "DaBaby, Roddy Ricch")]
    [InlineData("I Like It", "Cardi B", "I Like It", "Cardi B")]
    [InlineData("Don’t Matter To Me (with Michael Jackson)", "Drake", "Don't Matter To Me", "Drake, Michael Jackson")]
    [InlineData("Killer Queen - 2011 Remaster", "Queen", "Killer Queen (Remastered 2011)", "Queen")]
    [InlineData("Hollaback Girl", "Gwen Stefani", "Hollaback Girl", "Gwen Stefani")]
    [InlineData("JUGGERNAUT (feat. Lil Uzi Vert & Pharrell Williams)", "Tyler, The Creator", "JUGGERNAUT (feat. Lil Uzi Vert & Pharrell Williams)", "Tyler, The Creator")]
    [InlineData("Sorry 4 What? // LV BELT", "Tory Lanez", "Sorry 4 What? // LV BELT", "Tory Lanez")]
    [InlineData("Uwa!! So Temperate♫", "Toby Fox", "Uwa!! So Temperate", "Toby Fox")]
    [InlineData("Boku no Sensou", "Shinsei Kamattechan", "Boku no Sensou", "Shinsei Kamattechan")]
    [InlineData("僕の戦争", "神聖かまってちゃん", "僕の戦争", "神聖かまってちゃん")]
    public async Task ImportedTitlesUseRealDecisionRules(string title, string artist, string target, string targetArtist)
    {
        var gateway = Gateway(target, targetArtist);
        var result = (await Search(gateway, title, artist)).RequireValue();
        Assert.True(result.Decision.State == TrackMatchReviewState.Accepted && !result.Decision.RequiresReview,
            string.Join(",", result.Decision.Reasons.Concat(result.Decision.Warnings)));
    }

    [Theory]
    [InlineData("Song", "Song (Live)")]
    [InlineData("Song", "Song (Remix)")]
    [InlineData("Song (Remix)", "Song")]
    [InlineData("Song (Clean)", "Song (Explicit)")]
    public async Task DifferentEditionsAreNeverAccepted(string title, string target)
    {
        var result = (await Search(Gateway(target, "Artist"), title, "Artist")).RequireValue();
        Assert.True(result.Decision.State != TrackMatchReviewState.Accepted || result.Decision.RequiresReview);
    }

    // Spotify exports carry no song length, so nothing but the title and artist can tell these apart.
    [Theory]
    [InlineData("MIA (feat. Drake)", "Bad Bunny", "Otra Noche en Miami", "Bad Bunny")]
    [InlineData("Fade", "Alan Walker", "Faded", "Alan Walker")]
    [InlineData("Everyday", "Logic", "Everyday Cookin'", "Logic Levls")]
    [InlineData("Stay", "Artist", "Stays", "Artist")]
    public async Task ImportedPlayNeverTakesASimilarlyNamedSong(string title, string artist, string target, string targetArtist)
    {
        var result = (await Search(Gateway(target, targetArtist), title, artist, null)).RequireValue();
        Assert.True(result.Decision.State != TrackMatchReviewState.Accepted || result.Decision.RequiresReview);
    }

    [Theory]
    [InlineData("Going Bad (feat. Drake)", "Meek Mill", "Going Bad (feat. Drake)", "Meek Mill")]
    [InlineData("MIA (feat. Drake)", "Bad Bunny", "MIA", "Bad Bunny, Drake")]
    [InlineData("Godzilla (feat. Juice WRLD)", "Eminem", "Godzilla", "Eminem, Juice WRLD")]
    [InlineData("Drip Too Hard (Lil Baby & Gunna)", "Lil Baby", "Drip Too Hard", "Lil Baby, Gunna")]
    [InlineData("Happier", "Marshmello", "Happier", "Marshmello, Bastille")]
    [InlineData("Don’t Matter To Me (with Michael Jackson)", "Drake", "Don't Matter To Me", "Drake, Michael Jackson")]
    public async Task ImportedPlayWithoutALengthStillFindsTheRightSong(string title, string artist, string target, string targetArtist)
    {
        var result = (await Search(Gateway(target, targetArtist), title, artist, null)).RequireValue();
        Assert.True(result.Decision.State == TrackMatchReviewState.Accepted && !result.Decision.RequiresReview,
            string.Join(",", result.Decision.Reasons.Concat(result.Decision.Warnings)));
    }

    // Last.fm gives no length or album, so the artist credit is all that separates these two different recordings.
    [Theory]
    [InlineData("Charli xcx featuring julian casablancas", "Charli xcx, julian casablancas")]
    [InlineData("Charli xcx", "Charli xcx")]
    public void SparseMetadataStillPrefersTheRecordingWithTheRightCredits(string artist, string expected)
    {
        var user = Guid.NewGuid();
        var withFeature = new LocalTrackMatchCandidate(Guid.NewGuid(), user, "main", null, "with", null, "Mean girls", "Charli xcx, julian casablancas",
            "Brat and it's completely different but also still brat", null, 227_000, null, null, null, IsLocal: false);
        var solo = new LocalTrackMatchCandidate(Guid.NewGuid(), user, "main", null, "solo", null, "Mean girls", "Charli xcx", "BRAT", null, 189_000, null, null, null, IsLocal: false);
        var decision = new TrackMatchDecisionEngine().Decide(new(user, "main", null, 2, 1, new HashSet<string>()),
            new("source", "lastfm", "source", "Mean girls", artist, null, null, null, null, null, null), [withFeature, solo]);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        var selected = decision.Candidates.Single(item => item.LibraryTrackId == decision.SelectedLibraryTrackId);
        Assert.Equal(expected, selected.Artist);
        Assert.True(selected.Confidence > decision.Candidates.Single(item => item.LibraryTrackId != decision.SelectedLibraryTrackId).Confidence);
        Assert.True(selected.Confidence <= 0.94);
    }

    [Fact]
    public async Task HardSongsAreSearchedPastServicesThatDoNotHaveThem()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        string[] order = ["apple", "deezer", "qobuz", "monochrome", "tidal", "ytmusic"];
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(order);
        foreach (var provider in order)
            gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), provider, It.IsAny<string>(), 15))
                .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success(provider != "ytmusic" ? [] : [
                    new() { ExternalProvider = provider, ExternalId = "song", Title = "Going Bad (feat. Drake)", Artist = "Meek Mill", Album = "Championships" }]));
        var result = (await Search(gateway, "Going Bad (feat. Drake)", "Meek Mill", null)).RequireValue();
        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("ytmusic", result.SelectedExternal?.ExternalProvider);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task OneBrokenServiceOnlyHoldsASongBackWhenTooFewOthersAnswered(int answering, bool heldBack)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var answers = Enumerable.Range(0, answering).Select(index => $"answers-{index}").ToArray();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["broken", .. answers]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "broken", It.IsAny<string>(), 15))
            .ThrowsAsync(new HttpRequestException());
        foreach (var provider in answers)
            gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), provider, It.IsAny<string>(), 15))
                .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([]));
        var result = (await Search(gateway, "Song", "Artist", null)).RequireValue();
        Assert.Equal(heldBack, result.SearchFailure != null);
    }

    private static Mock<IProtocolProviderGateway> Gateway(string title, string artist)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([
                new() { ExternalProvider = "deezer", ExternalId = "song", Title = title, Artist = artist, Album = "Album", Duration = 180 }]));
        return gateway;
    }

    private static Task<ProviderOutcome<PlayableTrackMatch>> Search(Mock<IProtocolProviderGateway> gateway, string title, string artist, long? duration = 180000)
    {
        var user = Guid.NewGuid();
        return new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance)
            .MatchImportedHistoryAsync(new(ProtocolKind.Jellyfin, "main", "listener", null, "history-test", DateTimeOffset.UtcNow.AddMinutes(1), default),
                new("source", "history", "source", title, artist, "Album", null, duration, null, null, null),
                new(user, "main", null, 2, 1, new HashSet<string>()), default);
    }
}
