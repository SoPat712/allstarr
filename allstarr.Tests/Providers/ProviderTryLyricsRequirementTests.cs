using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Lyrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderTryLyricsRequirementTests
{
    [Theory]
    [InlineData("genius", "Fixture", null)]
    [InlineData("genius", null, "USQX91300108")]
    [InlineData("lyricsplus", "Fixture", null)]
    [InlineData("lyricsplus", null, "USQX91300108")]
    [InlineData("unison", "Fixture", null)]
    [InlineData("unison", null, "USQX91300108")]
    [InlineData("lrclib", "Fixture", null)]
    [InlineData("lrclib", null, "USQX91300108")]
    [InlineData("lrcred", "Fixture", null)]
    [InlineData("paxsenix", "Fixture", null)]
    public async Task SourcesRequiringArtist_ReturnSpecificGuidanceWithoutSending(string provider, string? title, string? isrc)
    {
        var handler = new RejectingHandler();
        var adapter = Adapter(provider, handler);
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderLyricsCapability>(provider, ProviderCapabilityKind.Lyrics)).Returns(adapter);
        var request = new ProviderTryRequest { Capability = "lyrics", Operation = "lyrics", Title = title, Isrc = isrc };
        Assert.Null(request.Validate());
        var result = await new ProviderTryOperations(registry.Object).ExecuteAsync(Context(provider), request);
        Assert.False(result.Succeeded);
        Assert.Equal("needs_artist", result.ReasonCode);
        Assert.Equal("This source needs the artist too", result.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("genius")]
    [InlineData("lyricsplus")]
    [InlineData("unison")]
    [InlineData("lrclib")]
    [InlineData("lrcred")]
    [InlineData("paxsenix")]
    public async Task SourcesRequiringArtist_EmptyInputKeepsUnavailableResult(string provider)
    {
        var handler = new RejectingHandler();
        var result = await Adapter(provider, handler).FetchLyricsAsync(Context(provider),
            new(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup")));
        Assert.True(result.IsSuccess);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("genius")]
    [InlineData("unison")]
    public async Task SourcesRequiringArtist_CachedMissDoesNotHideArtistGuidance(string provider)
    {
        var cache = new Mock<IApplicationCache>();
        cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.IsAny<string>()))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Unavailable, provider));
        var result = await Adapter(provider, new RejectingHandler(), cache.Object).FetchLyricsAsync(Context(provider),
            new(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"), trackTitle: "Fixture"));
        Assert.Equal("needs_artist", result.Error!.Code);
        Assert.Equal("This source needs the artist too", result.Error.SafeMessage);
    }

    private static IProviderLyricsCapability Adapter(string provider, HttpMessageHandler handler, IApplicationCache? cache = null) => provider switch
    {
        "genius" => new GeniusCapabilityAdapter(new HttpClient(handler), cache),
        "lyricsplus" => new LyricsPlusCapabilityAdapter(new HttpClient(handler), cache, _ => Task.FromResult("")),
        "unison" => new UnisonCapabilityAdapter(new HttpClient(handler), cache),
        "lrclib" => BuiltInLyricsCapabilityRegistration.CreateLrclib(Lrclib(handler)),
        "lrcred" => new LrcRedCapabilityAdapter(new HttpClient(handler), cache),
        "paxsenix" => new PaxsenixCapabilityAdapter(new HttpClient(handler), cache),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static LrclibService Lrclib(HttpMessageHandler handler)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient("Lrclib")).Returns(new HttpClient(handler));
        return new(clients.Object, new TestMemoryApplicationCache(), Options.Create(new CacheSettings()),
            NullLogger<LrclibService>.Instance);
    }

    private static ProviderExecutionContext Context(string provider) => new(
        new(ProviderActorKind.PublicRead, null), provider, null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, false, [provider]), "provider-try", "test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected request.");
        }
    }
}
