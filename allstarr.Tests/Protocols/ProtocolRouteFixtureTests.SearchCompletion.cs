using System.Net;
using System.Text.Json.Nodes;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_SearchClosesTheBodyBeforeSavingArtistCandidates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var links = new Mock<ILibraryArtistLinks>();
        links.Setup(item => item.RecordCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<Artist>>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await release.Task; });
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "finish", 20,
                false, false, true, false,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[]}"""), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryArtistLinks>();
            services.AddSingleton(links.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            using var response = await client.GetAsync("/allstarr/v1/search?q=finish&limit=20&types=MusicArtist&shape=ranked",
                HttpCompletionOption.ResponseHeadersRead, guard.Token);
            await entered.Task.WaitAsync(guard.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(guard.Token));
            var final = JsonNode.Parse((await reader.ReadLineAsync(guard.Token))!)!;
            Assert.True(final["done"]!.GetValue<bool>());
            Assert.Null(await reader.ReadLineAsync(guard.Token));
            Assert.False(release.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ranked")]
    public async Task ClientApi_SearchDeadlineStillFlushesDoneAndLateProviders(string? shape)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "deadline", 20,
                false, false, true, false,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, string _, int _, bool _, bool _, bool _, bool _,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("fast", new SearchResult(), [], token);
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                return (IReadOnlyList<string>)["slow"];
            });
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[]}"""), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new RecommendationClock(clock));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var responseTask = client.GetAsync($"/allstarr/v1/search?q=deadline&limit=20&types=MusicArtist{(shape == null ? "" : "&shape=" + shape)}",
            HttpCompletionOption.ResponseHeadersRead, guard.Token);
        await entered.Task.WaitAsync(guard.Token);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        using var response = await responseTask.WaitAsync(guard.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(guard.Token));
        Assert.NotNull(await reader.ReadLineAsync(guard.Token));
        clock.Advance(TimeSpan.FromMilliseconds(2350));
        var finalLine = await reader.ReadLineAsync(guard.Token);
        Assert.NotNull(finalLine);
        var final = JsonNode.Parse(finalLine)!;
        Assert.True(final["done"]!.GetValue<bool>());
        Assert.Equal("slow", Assert.Single(final["late"]!.AsArray())!.GetValue<string>());
        if (shape == "ranked") Assert.Equal("fast", Assert.Single(final["providers"]!.AsArray())!.GetValue<string>());
        Assert.Null(await reader.ReadLineAsync(guard.Token));
    }
}
