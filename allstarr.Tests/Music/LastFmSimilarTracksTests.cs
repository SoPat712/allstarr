using System.Net;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Models.Settings;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class LastFmSimilarTracksTests
{
    [Theory]
    [InlineData("single", "lastfm: ok", 1)]
    [InlineData("ok", "lastfm: ok", 2)]
    [InlineData("empty", "lastfm: empty", 0)]
    [InlineData("api-error", "lastfm: api-error-6", 0)]
    [InlineData("api-error-text", "lastfm: api-error", 0)]
    [InlineData("http", "lastfm: http-503", 0)]
    [InlineData("json", "lastfm: unavailable", 0)]
    [InlineData("odd", "lastfm: unavailable", 0)]
    [InlineData("exception", "lastfm: unavailable", 0)]
    [InlineData("missing-container", "lastfm: unavailable", 0)]
    [InlineData("missing-list", "lastfm: unavailable", 0)]
    [InlineData("null-list", "lastfm: unavailable", 0)]
    [InlineData("partial", "lastfm: unavailable", 0)]
    public async Task SimilarOutcomeUsesOnlyFixedTokensAndNumericCodes(string mode, string expectedOutcome, int expectedCount)
    {
        using var handler = new FixtureHandler(mode);
        using var client = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(client);
        var service = new LastFmSimilarTracks(clients.Object, new FixtureAccounts(), NullLogger<LastFmSimilarTracks>.Instance);

        var result = await service.FindWithOutcomeAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, default);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedCount, result.Suggestions.Count);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":null}""")]
    [InlineData("""{"apiKey":" "}""")]
    [InlineData("""{"apiKey":7}""")]
    public async Task MissingKeyReportsOutcomeWithoutCreatingAnHttpClient(string secretJson)
    {
        var accounts = new FixtureAccounts(secretJson);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var service = new LastFmSimilarTracks(clients.Object, accounts, NullLogger<LastFmSimilarTracks>.Instance);

        var result = await service.FindWithOutcomeAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, default);

        Assert.Equal("lastfm: no-key", result.Outcome);
        Assert.Empty(result.Suggestions);
        Assert.Equal(1, accounts.SecretReads);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TimeoutReportsOutcome()
    {
        var accounts = new Mock<IScopedRecommendationAccountAccessor>();
        accounts.Setup(item => item.HasAccountAsync(It.IsAny<IntelligenceScope>(), "lastfm", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = new LastFmSimilarTracks(Mock.Of<IHttpClientFactory>(), accounts.Object,
            NullLogger<LastFmSimilarTracks>.Instance);

        var result = await service.FindWithOutcomeAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, default);

        Assert.Equal("lastfm: timeout", result.Outcome);
        Assert.Empty(result.Suggestions);
    }

    [Theory]
    [InlineData("single", 1)]
    [InlineData("empty", 0)]
    [InlineData("api-error", 0)]
    [InlineData("odd", 0)]
    public async Task Similar_responses_distinguish_single_suggestions_from_empty_or_unusable_answers(string mode, int expected)
    {
        using var handler = new FixtureHandler(mode);
        using var client = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(client);
        var service = new LastFmSimilarTracks(clients.Object, new FixtureAccounts(), NullLogger<LastFmSimilarTracks>.Instance);

        Assert.Equal(expected, (await service.FindAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, default)).Count);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("Juice WRLD [A]", "⭐ Up Up and Away [A]/[E]", "Juice WRLD", "Up Up and Away")]
    [InlineData("Main feat. Guest [E]", "Song (feat. Guest) [A]", "Main feat. Guest", "Song (feat. Guest)")]
    public async Task Similar_request_removes_presentation_labels_and_preserves_credits(string artist, string title, string expectedArtist, string expectedTitle)
    {
        using var handler = new FixtureHandler("ok");
        using var client = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(client);
        var service = new LastFmSimilarTracks(clients.Object, new FixtureAccounts(), NullLogger<LastFmSimilarTracks>.Instance);

        Assert.Equal(2, (await service.FindAsync(MusicObservationsTests.Context(), artist, title, 100, default)).Count);

        var uri = Assert.IsType<Uri>(handler.RequestUri);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("ws.audioscrobbler.com", uri.Host);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal("track.getsimilar", query["method"]);
        Assert.Equal("1", query["autocorrect"]);
        Assert.Equal("json", query["format"]);
        Assert.Equal("50", query["limit"]);
        Assert.Equal(expectedArtist, query["artist"]);
        Assert.Equal(expectedTitle, query["track"]);
        Assert.Equal("fixture-key", query["api_key"]);
    }

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

        var result = await service.FindWithOutcomeAsync(context, "Artist", "Title", 5, default);

        Assert.Equal("lastfm: no-account", result.Outcome);
        Assert.Empty(result.Suggestions);

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

        var result = await service.FindWithOutcomeAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, default);

        Assert.Equal("lastfm: disabled", result.Outcome);
        Assert.Empty(result.Suggestions);

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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.FindWithOutcomeAsync(MusicObservationsTests.Context(), "Artist", "Title", 5, cancellation.Token));
    }

    private sealed class FixtureAccounts(string secretJson = """{"apiKey":"fixture-key"}""") : IScopedRecommendationAccountAccessor
    {
        public int SecretReads { get; private set; }
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            SecretReads++;
            using var secret = JsonDocument.Parse(secretJson);
            return await operation(secret.RootElement, cancellationToken);
        }
    }

    private sealed class FixtureHandler(string mode) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? RequestUri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUri = request.RequestUri;
            if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (mode == "exception") throw new HttpRequestException("fixture");
            return new HttpResponseMessage(mode == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(mode switch
                {
                    "json" => "invalid",
                    "single" => """{"similartracks":{"track":{"name":"Only song","artist":{"name":"Only artist"}}}}""",
                    "empty" => """{"similartracks":{"track":[]}}""",
                    "api-error" => """{"error":6,"message":"fixture-key https://fixture.invalid/private"}""",
                    "api-error-text" => """{"error":"fixture-key https://fixture.invalid/private","message":"private"}""",
                    "odd" => """{"similartracks":{"track":"unexpected"}}""",
                    "missing-container" => "{}",
                    "missing-list" => """{"similartracks":{}}""",
                    "null-list" => """{"similartracks":{"track":null}}""",
                    "partial" => """{"similartracks":{"track":[{"name":"Valid","artist":{"name":"Artist"}},{"name":"Incomplete"}]}}""",
                    _ => """
                    {"similartracks":{"track":[
                      {"name":"First song","artist":{"name":"First artist"}},
                      {"name":"Second song","artist":{"name":"Second artist"}}
                    ]}}
                    """
                })
            };
        }
    }
}
