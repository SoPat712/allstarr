using System.Net;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class PersonalMixSeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_candidates_combine_provider_and_similar_track_suggestions(bool artist)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = new Song { Title = "Seed", Artist = "Seed artist", ExternalProvider = "deezer", ExternalId = "seed" };
        var providerSong = new Song { Title = "Provider song", Artist = "Provider artist" };
        var suggested = new Song { Title = "Suggested", Artist = "Related artist" };
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 100))
            .ReturnsAsync([providerSong]);
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Suggested Related artist", 3))
            .ReturnsAsync([suggested, new Song { Title = "Wrong edition", Artist = "Other" }]);
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "artist"))
            .ReturnsAsync([seed]);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(() => new HttpClient(new Suggestions()));
        var similar = new LastFmSimilarTracks(clients.Object, new Accounts(), NullLogger<LastFmSimilarTracks>.Instance);
        var radio = new MusicMixService(gateway.Object, similar);
        var result = await radio.SeedCandidatesAsync(MusicObservationsTests.Context(), artist ? null : seed,
            artist ? new Artist { Name = "Seed artist" } : null, "deezer", artist ? "artist" : "seed", default);
        Assert.Contains(providerSong, result);
        Assert.Contains(suggested, result);
        Assert.DoesNotContain(result, song => song.Title == "Wrong edition");
    }

    private sealed class Accounts : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken token) => Task.FromResult(true);
        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken token)
        {
            using var secret = JsonDocument.Parse("{\"apiKey\":\"fixture-key\"}");
            return await operation(secret.RootElement, token);
        }
    }

    private sealed class Suggestions : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"similartracks\":{\"track\":[{\"name\":\"Suggested\",\"artist\":{\"name\":\"Related artist\"}}]}}") });
    }
}
