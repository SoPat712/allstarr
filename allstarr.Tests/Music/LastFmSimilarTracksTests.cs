using System.Net;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Models.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class LastFmSimilarTracksTests
{
    [Fact]
    public async Task NoAccountDoesNotResolveSecretsOrCreateAnHttpClient()
    {
        var context = MusicObservationsTests.Context();
        var accounts = new Mock<IScopedRecommendationAccountAccessor>(MockBehavior.Strict);
        accounts.Setup(item => item.HasAccountAsync(
                It.Is<IntelligenceScope>(scope => scope.OwnerUserId == context.Principal!.UserId &&
                    scope.Protocol == "jellyfin" && scope.BackendInstanceId == "fixture"),
                "lastfm", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var service = new LastFmSimilarTracks(clients.Object, accounts.Object, NullLogger<LastFmSimilarTracks>.Instance);

        Assert.Empty(await service.FindAsync(context, "Artist", "Title", 5, default));

        accounts.VerifyAll();
        accounts.VerifyNoOtherCalls();
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisabledDoesNotReadAccountOrCreateAnHttpClient()
    {
        var accounts = new Mock<IScopedRecommendationAccountAccessor>(MockBehavior.Strict);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var service = new LastFmSimilarTracks(clients.Object, accounts.Object,
            NullLogger<LastFmSimilarTracks>.Instance,
            Options.Create(new PlaybackSettings { LastFmSimilarEnabled = false }));

        Assert.Empty(await service.FindAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, default));

        accounts.VerifyNoOtherCalls();
        clients.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    [InlineData("timeout")]
    [InlineData("exception")]
    public async Task OptionalFailureReturnsNoSuggestions(string failure)
    {
        var accounts = new FixtureAccounts();
        using var handler = new FixtureHandler(failure);
        using var client = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(client);
        var service = new LastFmSimilarTracks(clients.Object, accounts, NullLogger<LastFmSimilarTracks>.Instance);

        Assert.Empty(await service.FindAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, accounts.SecretReads);
    }

    [Fact]
    public async Task ValidSuggestionsKeepTheirOrderAndRespectTheLimit()
    {
        using var handler = new FixtureHandler("ok");
        using var client = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(client);
        var service = new LastFmSimilarTracks(clients.Object, new FixtureAccounts(), NullLogger<LastFmSimilarTracks>.Instance);

        var result = await service.FindAsync(MusicObservationsTests.Context(), "Artist", "Title", 1, default);

        Assert.Equal(new SimilarTrackSuggestion("First artist", "First song"), Assert.Single(result));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var accounts = new Mock<IScopedRecommendationAccountAccessor>();
        accounts.Setup(item => item.HasAccountAsync(It.IsAny<IntelligenceScope>(), "lastfm", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var service = new LastFmSimilarTracks(Mock.Of<IHttpClientFactory>(), accounts.Object,
            NullLogger<LastFmSimilarTracks>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.FindAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, cancellation.Token));
    }

    private sealed class FixtureAccounts : IScopedRecommendationAccountAccessor
    {
        public int SecretReads { get; private set; }
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            SecretReads++;
            using var secret = JsonDocument.Parse("""{"apiKey":"fixture-key"}""");
            return await operation(secret.RootElement, cancellationToken);
        }
    }

    private sealed class FixtureHandler(string mode) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (mode == "exception") throw new HttpRequestException("fixture");
            return new HttpResponseMessage(mode == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(mode == "json" ? "invalid" : """
                    {"similartracks":{"track":[
                      {"name":"First song","artist":{"name":"First artist"}},
                      {"name":"Second song","artist":{"name":"Second artist"}}
                    ]}}
                    """)
            };
        }
    }
}
