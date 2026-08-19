using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolLyricsResolverTests
{
    [Fact]
    public async Task Resolver_DeezerTrackFallsBackToLrclibWithFullMetadata()
    {
        var protocol = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "backend",
            "api-key",
            null,
            "lyrics-test",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics))
            .Returns(["spotify", "lrclib"]);
        providers.Setup(item => item.GetLyricsAsync(
                protocol,
                "lrclib",
                It.Is<string>(value => value.Length == 64),
                ProviderLyricsFormat.LineTimed,
                "Fixture song",
                It.Is<IReadOnlyList<string>>(artists => artists.SequenceEqual(
                    new[] { "First artist", "Second artist" })),
                "Fixture album",
                180))
            .ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available,
                "lrclib",
                ProviderLyricsFormat.LineTimed,
                "[00:01.00]Fixture line\n",
                "sha256:fixture"));
        var resolver = new ProtocolLyricsResolver(
            providers.Object,
            NullLogger<ProtocolLyricsResolver>.Instance);

        var result = await resolver.FindAsync(protocol, new Song
        {
            Title = "Fixture song",
            Artist = "First artist",
            Artists = ["First artist", "Second artist"],
            Album = "Fixture album",
            Duration = 180
        }, "ext-deezer-song-3135556", "deezer", "3135556");

        Assert.NotNull(result);
        Assert.Equal("[00:01.00]Fixture line\n", result.SyncedLyrics);
        Assert.Null(result.PlainLyrics);
        Assert.Equal("lrclib", result.Source);
        Assert.Equal("sha256:fixture", result.Revision);
        providers.Verify(item => item.GetLyricsAsync(
            It.IsAny<ProtocolExecutionContext>(), "spotify", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task Resolver_TriesExactSourceProviderBeforeConfiguredFallbacks()
    {
        var protocol = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin, "backend", "api-key", null, "lyrics-source-first",
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics))
            .Returns(["lrclib", "spotiflac-apple-music"]);
        providers.Setup(item => item.GetLyricsAsync(
                protocol, "spotiflac-apple-music", "apple-track", ProviderLyricsFormat.LineTimed,
                "Fixture song", It.IsAny<IReadOnlyList<string>>(), "Fixture album", 240))
            .ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "apple-music",
                ProviderLyricsFormat.LineTimed, "[00:01.00]Apple line\n"));
        var resolver = new ProtocolLyricsResolver(
            providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);

        var result = await resolver.FindAsync(
            protocol,
            new Song { Title = "Fixture song", Artist = "Artist", Album = "Fixture album", Duration = 240 },
            "library-song-id",
            "spotiflac-apple-music",
            "apple-track");

        Assert.Equal("apple-music", result?.Source);
        providers.Verify(item => item.GetLyricsAsync(
            It.IsAny<ProtocolExecutionContext>(), "lrclib", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task Resolver_TranslatesADeezerIdentityAcrossConfiguredLyricsFallbacks()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IOptions<CacheSettings>>(Options.Create(new CacheSettings()))
            .BuildServiceProvider();
        CacheExtensions.InitializeCacheSettings(services);
        var protocol = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin, "backend", "api-key", null, "lyrics-translation",
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics))
            .Returns(["spotify", "apple-download", "lrclib"]);
        providers.Setup(item => item.GetLyricsAsync(
                protocol, "spotify", "2takcwOaAZWiXQijPHIx7B", ProviderLyricsFormat.LineTimed,
                "Rocket", It.IsAny<IReadOnlyList<string>>(), "Fixture album", 210))
            .ReturnsAsync((ProviderLyricsResult?)null);
        providers.Setup(item => item.GetLyricsAsync(
                protocol, "apple-download", "2037093408", ProviderLyricsFormat.LineTimed,
                "Rocket", It.IsAny<IReadOnlyList<string>>(), "Fixture album", 210))
            .ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "apple-download",
                ProviderLyricsFormat.LineTimed, "[00:01.00]Rocket line\n"));
        var handler = new JsonHandler(
            """{"linksByPlatform":{"spotify":{"url":"https://open.spotify.com/track/2takcwOaAZWiXQijPHIx7B"},"appleMusic":{"url":"https://music.apple.com/us/album/fixture?i=2037093408"}}}""");
        var cache = new Mock<IApplicationCache>();
        cache.Setup(item => item.GetAsync<string>(It.IsAny<string>())).ReturnsAsync((string?)null);
        cache.Setup(item => item.SetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync(true);
        var odesli = new OdesliService(
            new HttpFactory(handler),
            NullLogger<OdesliService>.Instance,
            cache.Object);
        var resolver = new ProtocolLyricsResolver(
            providers.Object, NullLogger<ProtocolLyricsResolver>.Instance, odesli);

        var result = await resolver.FindAsync(
            protocol,
            new Song { Title = "Rocket", Artist = "Artist", Album = "Fixture album", Duration = 210 },
            "ext-deezer-song-13190193",
            "deezer",
            "13190193");

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(
            "spotify:2takcwOaAZWiXQijPHIx7B|apple-download:2037093408",
            string.Join('|', providers.Invocations
                .Where(call => call.Method.Name == nameof(IProtocolProviderGateway.GetLyricsAsync))
                .Select(call => $"{call.Arguments[1]}:{call.Arguments[2]}")));
        Assert.Equal("apple-download", result?.Source);
        Assert.Equal("[00:01.00]Rocket line\n", result?.SyncedLyrics);
        providers.Verify(item => item.GetLyricsAsync(
            It.IsAny<ProtocolExecutionContext>(), "lrclib", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
