using System.Net;
using allstarr.Core.Protocols;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinSearchLibraryScopeTimingKeepsBackendFloorVisible()
    {
        var backendMs = 0.0;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var authentication = request.RequestUri!.AbsolutePath == "/Users/Me";
            Thread.Sleep(authentication ? 150 : 164);
            backendMs += timer.Elapsed.TotalMilliseconds;
            return authentication ? Json(200, """{"Id":"listener"}""") :
                Json(200, """{"Items":[{"Id":"native","Type":"Audio","Name":"Track"}],"TotalRecordCount":1,"StartIndex":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        const string path = "/Items?searchTerm=a&allstarrScope=library&Limit=40&ApiKey=listener";
        using (var warmup = await client.GetAsync(path)) Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        var samples = new List<double>();
        var overhead = new List<double>();
        for (var index = 0; index < 3; index++)
        {
            backendMs = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            samples.Add(timer.Elapsed.TotalMilliseconds);
            overhead.Add(timer.Elapsed.TotalMilliseconds - backendMs);
        }
        gateway.VerifyNoOtherCalls();
        _lyricsSearchOutput.WriteLine($"Library scope n=3: p50={samples.Order().ElementAt(1):F1} ms; p50 processing excluding fake backend/auth={overhead.Order().ElementAt(1):F1} ms. Native delay=164 ms, auth delay=150 ms; provider calls=0.");
    }

    [Fact]
    public async Task SearchSavedIdentityProjectionResolvesMatchesInOneBatch()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        var recording = Guid.NewGuid();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(service => service.ResolveRecordingsAsync(It.IsAny<ProviderActorContext>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderActorContext _, IReadOnlyCollection<string> ids, CancellationToken _) =>
                (IReadOnlyDictionary<string, CanonicalRecordingIdentity?>)ids.ToDictionary(id => id,
                    _ => (CanonicalRecordingIdentity?)new(recording, FavoriteViewer, null, null, 1)));
        identities.Setup(service => service.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(),
            "ext-deezer-song-saved", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanonicalRecordingIdentity(recording, FavoriteViewer, null, null, 1));
        var songs = new[] { "spotify", "qobuz" }.Select(provider => new Song
        {
            Id = $"ext-{provider}-song-original",
            ExternalProvider = provider,
            ExternalId = "original",
            Title = "Saved song",
            Artist = "Saved artist",
            Album = "Saved album",
            Duration = 180
        }).ToArray();
        foreach (var song in songs)
            await identities.Object.ResolveRecordingAsync(SavedMembershipContext().RequireActor(), "ext-deezer-song-saved");
        var beforeCalls = identities.Invocations.Count;
        Assert.Equal(2, beforeCalls);
        identities.Invocations.Clear();
        await new SavedLibraryReader(storage, identities: identities.Object)
            .PreferSavedIdsAsync(SavedMembershipContext(), songs, [], default);
        Assert.All(songs, song =>
        {
            Assert.Equal("ext-deezer-song-saved", song.Id);
            Assert.Equal(recording, song.CanonicalRecordingId);
        });
        identities.Verify(service => service.ResolveRecordingsAsync(It.IsAny<ProviderActorContext>(),
            It.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
        identities.VerifyNoOtherCalls();
        _lyricsSearchOutput.WriteLine($"Saved search identity reads: legacy per-song loop={beforeCalls}; batched projection=1; output IDs/recording IDs identical.");
    }

    [Fact]
    public async Task JellyfinSearchLibraryScopePreservesNativeRowsAndSkipsProviders()
    {
        const string expected = """{"Items":[{"Id":"native","Type":"Audio","Name":"Track","FutureField":{"Value":7}}],"TotalRecordCount":1,"StartIndex":0}""";
        string? forwarded = null;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            forwarded = request.RequestUri.PathAndQuery;
            return Json(200, expected);
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?searchTerm=a&allstarrScope=library&Fields=MediaSources&Fields=ChildCount&Limit=40&ApiKey=listener");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("allstarrScope", forwarded!);
        Assert.Contains("Fields=MediaSources&Fields=ChildCount", forwarded!);
        gateway.VerifyNoOtherCalls();
    }
}
