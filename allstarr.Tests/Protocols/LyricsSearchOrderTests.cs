using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class LyricsSearchOrderTests
{
    private const string Title = "Nothing's Gonna Stop Us Now";

    [Fact]
    public async Task EqualHitsGoYourCopyFirstThenTheOneYourServicesListHighestForTheTitle()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var gateway = new Mock<IProtocolProviderGateway>();
        // Each service lists the original first; covers come lower or not at all.
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), Title, 40, 0, 0, null)).ReturnsAsync(new SearchResult
        {
            Songs =
            [
                Copy("spotiflac-deezer", Title, "Starship"), Copy("spotiflac-deezer", "Something Else", "Dream Tunes"),
                Copy("spotiflac-deezer", Title, "Max Oazo"), Copy("spotiflac-apple-music", Title, "Starship"),
                Copy("spotiflac-apple-music", Title, "Dream Tunes")
            ]
        });
        var order = new LyricsSearchOrder(gateway.Object, new TestMemoryApplicationCache(),
            new ProviderCheckServiceTests.Factory(database.Options), NullLogger<LyricsSearchOrder>.Instance);
        var hits = new[]
        {
            ("Dream Tunes", 1.0, false), ("Max Oazo", 1.0, false), ("Starship", 1.0, false), ("Jefferson Airplane", 1.0, false),
            ("Somebody Else", 0.4, false)
        };

        var ordered = await order.OrderAsync(Execution(), hits, hit => hit.Item2 >= 0.95,
            hit => new(Title, hit.Item1, hit.Item3), TimeSpan.FromSeconds(5));
        var owned = await order.OrderAsync(Execution(), hits.Select(hit => hit.Item1 == "Max Oazo" ? hit with { Item3 = true } : hit).ToArray(),
            hit => hit.Item2 >= 0.95, hit => new(Title, hit.Item1, hit.Item3), TimeSpan.FromSeconds(5));

        Assert.Equal(["Starship", "Dream Tunes", "Max Oazo", "Jefferson Airplane", "Somebody Else"], ordered.Select(hit => hit.Item1));
        Assert.Equal(["Max Oazo", "Starship", "Dream Tunes", "Jefferson Airplane", "Somebody Else"], owned.Select(hit => hit.Item1));
        // The title search is asked once and kept for the next line.
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), Title, 40, 0, 0, null), Times.Once);
    }

    [Fact]
    public async Task ASlowTitleSearchKeepsTheIndexOrderUntilItAnswers()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var answer = new TaskCompletionSource<SearchResult>();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), Title, 40, 0, 0, null)).Returns(answer.Task);
        var order = new LyricsSearchOrder(gateway.Object, new TestMemoryApplicationCache(),
            new ProviderCheckServiceTests.Factory(database.Options), NullLogger<LyricsSearchOrder>.Instance);
        string[] hits = ["Dream Tunes", "Starship"];

        var listener = Guid.CreateVersion7();
        var first = await order.OrderAsync(Execution(listener), hits, _ => true, hit => new(Title, hit, false), TimeSpan.FromMilliseconds(50));
        answer.SetResult(new SearchResult { Songs = [Copy("spotiflac-deezer", Title, "Starship"), Copy("spotiflac-deezer", Title, "Dream Tunes")] });
        var later = await order.OrderAsync(Execution(listener), hits, _ => true, hit => new(Title, hit, false), TimeSpan.FromSeconds(5));

        Assert.Equal(["Dream Tunes", "Starship"], first);
        Assert.Equal(["Starship", "Dream Tunes"], later);
    }

    private static Song Copy(string provider, string title, string artist) =>
        new() { Id = $"ext-{provider}-song-{artist}", Title = title, Artist = artist, ExternalProvider = provider, Duration = 270 };

    private static ProtocolExecutionContext Execution(string owner = "018f2f6e-0000-7000-8000-000000000001") => Execution(Guid.Parse(owner));

    private static ProtocolExecutionContext Execution(Guid owner)
    {
        return new(ProtocolKind.Jellyfin, "backend", "listener", new AllstarrPrincipal(owner, "jellyfin", "backend", "listener", "Owner", false),
            "lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }
}
