using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class PersonalMixSeedTests
{
    [Fact]
    public async Task Custom_candidate_caps_keep_top_tracks_and_sample_each_related_artist()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = Track("seed", "Seed artist");
        seed.ArtistId = "ext-deezer-artist-root";
        var own = Enumerable.Range(0, 50).Select(index => Track("own-" + index, seed.Artist)).ToList();
        var similar = Enumerable.Range(0, 100).Select(index => Track("similar-" + index, "Radio artist " + index)).ToList();
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 100))
            .ReturnsAsync(similar.Prepend(seed).Append(similar[0]).ToList());
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root")).ReturnsAsync(own);
        gateway.Setup(item => item.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "root", 10))
            .ReturnsAsync(Enumerable.Range(0, 10).Select(index => new Artist
            { Name = "Related artist " + index, ExternalProvider = "deezer", ExternalId = index.ToString() }).ToList());
        var related = new List<Song>();
        for (var index = 0; index < 10; index++)
        {
            var artist = index.ToString();
            var tracks = Enumerable.Range(0, 50).Select(track => Track("related-" + artist + "-" + track, "Related artist " + artist)).ToList();
            related.AddRange(tracks.Take(2));
            gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", artist)).ReturnsAsync(tracks);
        }
        using var radio = new MusicMixService(gateway.Object);

        var candidates = await radio.SeedCandidatesAsync(MusicObservationsTests.Context(), seed, null, "deezer", "seed", default, capCandidates: true);

        Assert.Equal(60, candidates.Count);
        Assert.DoesNotContain(seed, candidates);
        Assert.Equal(60, candidates.Select(song => song.Id).Distinct().Count());
        Assert.Equal(own.Take(4), candidates.Where(song => song.Artist == seed.Artist));
        Assert.Equal(similar.Take(36), candidates.Where(song => song.ExternalId!.StartsWith("similar-", StringComparison.Ordinal)));
        Assert.Equal(related.OrderBy(song => song.Id, StringComparer.Ordinal), candidates.Where(song => song.ExternalId!.StartsWith("related-", StringComparison.Ordinal))
            .OrderBy(song => song.Id, StringComparer.Ordinal));

        static Song Track(string id, string artist) => new()
        { Id = "ext-deezer-song-" + id, ExternalProvider = "deezer", ExternalId = id, Title = "Track " + id, Artist = artist };
    }

    [Fact]
    public async Task Provider_seed_resolution_keeps_the_requested_recording_edition()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = new Song { Title = "Seed [A]/[E]", Artist = "Artist [A]", Duration = 180, ExplicitContentLyrics = 1 };
        var clean = new Song { Title = "Seed", Artist = "Artist", Duration = 180, ExplicitContentLyrics = 0, ExternalProvider = "deezer", ExternalId = "clean" };
        var explicitSong = new Song { Title = "Seed", Artist = "Artist", Duration = 180, ExplicitContentLyrics = 1, ExternalProvider = "deezer", ExternalId = "explicit" };
        var related = new Song { Title = "Related", Artist = "Related artist" };
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "Seed Artist", 3))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([clean, explicitSong]));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "explicit", 100)).ReturnsAsync([related]);
        using var radio = new MusicMixService(gateway.Object);

        Assert.Contains(related, await radio.SeedCandidatesAsync(MusicObservationsTests.Context(), seed, null, null, null, default));
        gateway.Verify(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "clean", 100), Times.Never);
    }

    [Fact]
    public async Task Similar_track_lookup_starts_while_provider_seed_resolution_is_pending()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = new Song { Title = "Seed", Artist = "Seed artist", Duration = 180 };
        var resolved = new Song { Title = seed.Title, Artist = seed.Artist, Duration = 180, ExternalProvider = "deezer", ExternalId = "seed" };
        var release = new TaskCompletionSource<ProviderOutcome<IReadOnlyList<Song>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "Seed Seed artist", 3)).Returns(release.Task);
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Seed artist", 3))
            .Returns(async () => (await release.Task).RequireValue());
        var clients = new Mock<IHttpClientFactory>();
        using var client = new HttpClient(new Suggestions(() => requested.TrySetResult()));
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(client);
        var similar = new LastFmSimilarTracks(clients.Object, new Accounts(), NullLogger<LastFmSimilarTracks>.Instance);
        using var radio = new MusicMixService(gateway.Object, similar);

        var pending = radio.SeedCandidatesAsync(MusicObservationsTests.Context(), seed, null, null, null, default);
        var overlapped = await Task.WhenAny(requested.Task, Task.Delay(TimeSpan.FromSeconds(2))) == requested.Task;
        release.TrySetResult(ProviderOutcome<IReadOnlyList<Song>>.Success([resolved]));
        await pending;

        Assert.True(overlapped, "Similar-track discovery should not wait for provider seed resolution.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Seed_candidates_fall_back_independently_for_radio_and_artist_tracks(bool external, bool firstRadio)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var seed = new Song { Title = "Up Up and Away", Artist = "Juice WRLD", Duration = 180 };
        var first = new Song { Title = seed.Title, Artist = seed.Artist, Duration = 180, ExternalProvider = "apple-musickit", ExternalId = "first" };
        var second = new Song { Title = seed.Title, Artist = seed.Artist, Duration = 180, ExternalProvider = "deezer", ExternalId = "second" };
        var radioSong = new Song { Title = "Radio song", Artist = "Related artist" };
        var artistSong = new Song { Title = "Artist song", Artist = seed.Artist };
        var relatedSong = new Song { Title = "Related song", Artist = "Related artist" };
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["apple-musickit", "deezer", "qobuz"]);
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Up Up and Away Juice WRLD", 3))
            .ReturnsAsync([first]);
        foreach (var match in new[] { first, second })
        {
            gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), match.ExternalProvider!, "Up Up and Away Juice WRLD", 3))
                .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([match]));
            gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), seed.Artist, 0, 0, 3, match.ExternalProvider))
                .ReturnsAsync(new SearchResult { Artists = [new() { Name = seed.Artist, ExternalProvider = match.ExternalProvider, ExternalId = "artist" }] });
        }
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "apple-musickit", "first", 100))
            .ReturnsAsync(firstRadio ? [radioSong] : []);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "second", 100)).ReturnsAsync([radioSong]);
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "apple-musickit", "artist")).ReturnsAsync([]);
        gateway.Setup(item => item.GetArtistTracksAsync(It.Is<ProtocolExecutionContext>(context => !context.IsReadOnly), "deezer", "artist"))
            .ReturnsAsync([artistSong]);
        gateway.Setup(item => item.GetSimilarArtistsAsync(It.Is<ProtocolExecutionContext>(context => !context.IsReadOnly), "deezer", "artist", 10))
            .ReturnsAsync([new() { Name = "Related artist", ExternalProvider = "deezer", ExternalId = "related" }]);
        gateway.Setup(item => item.GetArtistTracksAsync(It.Is<ProtocolExecutionContext>(context => !context.IsReadOnly), "deezer", "related"))
            .ReturnsAsync([relatedSong]);
        var logger = new SeedLogger();
        using var radio = new MusicMixService(gateway.Object);
        var diagnostics = new SeedMixDiagnostics(logger);

        var result = await radio.SeedCandidatesAsync(MusicObservationsTests.Context() with { IsReadOnly = true }, external ? first : seed,
            null, external ? "apple-musickit" : null, external ? "first" : null, default, diagnostics);
        diagnostics.Dispose();

        Assert.Equal([radioSong, artistSong, relatedSong], result);
        Assert.Contains("provider similar=1, artist tracks=1, related artists=1, related tracks=1", Assert.Single(logger.Messages));
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "qobuz", It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        gateway.Verify(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "second", 100), firstRadio ? Times.Never : Times.Once);
    }

    [Fact]
    public async Task Artist_seed_falls_back_when_the_original_provider_has_no_artist_tracks()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["apple-musickit", "deezer"]);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed artist", 0, 0, 3, "deezer"))
            .ReturnsAsync(new SearchResult
            {
                Artists = [new() { Name = "Wrong artist", ExternalProvider = "deezer", ExternalId = "wrong" },
                    new() { Name = "Seed artist", ExternalProvider = "deezer", ExternalId = "right" }]
            });
        var track = new Song { Title = "Artist song", Artist = "Seed artist", ExternalProvider = "deezer", ExternalId = "track" };
        gateway.Setup(item => item.GetArtistTracksAsync(It.Is<ProtocolExecutionContext>(context => !context.IsReadOnly), "deezer", "right"))
            .ReturnsAsync([track]);
        using var radio = new MusicMixService(gateway.Object);

        var result = await radio.SeedCandidatesAsync(MusicObservationsTests.Context() with { IsReadOnly = true }, null,
            new Artist { Name = "Seed artist" }, "apple-musickit", "original", default);

        Assert.Contains(track, result);
        gateway.Verify(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "wrong"), Times.Never);
    }

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
        var logger = new SeedLogger();
        var diagnostics = new SeedMixDiagnostics(logger);
        var result = await radio.SeedCandidatesAsync(MusicObservationsTests.Context(), artist ? null : seed,
            artist ? new Artist { Name = "Seed artist" } : null, "deezer", artist ? "artist" : "seed", default, diagnostics);
        diagnostics.Dispose();
        Assert.Contains(providerSong, result);
        Assert.Contains(suggested, result);
        Assert.DoesNotContain(result, song => song.Title == "Wrong edition");
        var message = Assert.Single(logger.Messages);
        Assert.Contains("seed resolved=1, seed search found=0, provider similar=1", message);
        Assert.Contains("similar tracks=1, suggestions found=2, suggestions resolved=1", message);
        Assert.Contains("artist tracks=" + (artist ? 1 : 0), message);
        Assert.DoesNotContain("Suggested", message);
        Assert.DoesNotContain("Related artist", message);
        Assert.DoesNotContain("fixture-key", message);
    }

    private sealed class SeedLogger : ILogger<PersonalMixService>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
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

    private sealed class Suggestions(Action? requested = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            requested?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"similartracks\":{\"track\":[{\"name\":\"Suggested\",\"artist\":{\"name\":\"Related artist\"}}]}}") });
        }
    }
}
