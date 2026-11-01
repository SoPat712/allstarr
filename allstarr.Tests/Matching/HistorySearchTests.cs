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

    private static Mock<IProtocolProviderGateway> Gateway(string title, string artist)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([
                new() { ExternalProvider = "deezer", ExternalId = "song", Title = title, Artist = artist, Album = "Album", Duration = 180 }]));
        return gateway;
    }

    private static Task<ProviderOutcome<PlayableTrackMatch>> Search(Mock<IProtocolProviderGateway> gateway, string title, string artist)
    {
        var user = Guid.NewGuid();
        return new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance)
            .MatchImportedHistoryAsync(new(ProtocolKind.Jellyfin, "main", "listener", null, "history-test", DateTimeOffset.UtcNow.AddMinutes(1), default),
                new("source", "history", "source", title, artist, "Album", null, 180000, null, null, null),
                new(user, "main", null, 2, 1, new HashSet<string>()), default);
    }
}
