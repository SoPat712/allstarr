using System.Net;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Capabilities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ListenBrainzSimilarTracksTests
{
    internal const string Mbid = "8a49dba0-253a-4535-b87f-78bb035336ce";

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task MissingIdentityDoesNotCallHttp(string? mbid)
    {
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var service = new ListenBrainzSimilarTracks(clients.Object, Identities(mbid), new TestMemoryApplicationCache(),
            NullLogger<ListenBrainzSimilarTracks>.Instance);
        Assert.Empty(await service.FindAsync(MusicObservationsTests.Context(), "seed", 10, default));
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SuggestionsAreBoundedFilteredAndCachedByRecording()
    {
        using var handler = new Handler("ok");
        var service = Create(handler);
        var context = MusicObservationsTests.Context();
        var first = await service.FindAsync(context, "seed", 1, default);
        Assert.Equal(new SimilarTrackSuggestion("Artist", "First"), Assert.Single(first));
        var second = await service.FindAsync(context, "seed", 10, default);
        Assert.Equal(new[] { "First", "Second" }, second.Select(item => item.Title));
        Assert.Equal(1, handler.Calls);
        Assert.Contains("recording_mbids=" + Mbid, handler.Url);
        Assert.StartsWith("https://labs.api.listenbrainz.org/similar-recordings/json?", handler.Url);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    [InlineData("timeout")]
    public async Task OptionalFailureReturnsNoSuggestions(string mode)
    {
        using var handler = new Handler(mode);
        Assert.Empty(await Create(handler).FindAsync(MusicObservationsTests.Context(), "seed", 10, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var handler = new Handler("timeout");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(handler).FindAsync(MusicObservationsTests.Context(), "seed", 10, cancellation.Token));
    }

    internal static ListenBrainzSimilarTracks Create(Handler handler)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(ListenBrainzSimilarTracks))).Returns(() => new HttpClient(handler, false));
        return new(clients.Object, Identities(Mbid), new TestMemoryApplicationCache(), NullLogger<ListenBrainzSimilarTracks>.Instance);
    }

    private static ITrackIdentityService Identities(string? mbid)
    {
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(), "seed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanonicalRecordingIdentity(Guid.NewGuid(), Guid.NewGuid(), null, mbid, 1));
        return identities.Object;
    }

    internal sealed class Handler(string mode) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Url { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.ToString();
            if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(mode == "http" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                Content = new StringContent(mode == "json" ? "bad JSON" : $$"""
                    [{"reference_mbid":"{{Mbid}}","recording_mbid":"f3bba4cd-8018-468b-902e-bc8f029593e5","recording_name":"First","artist_credit_name":"Artist"},
                     {"reference_mbid":"{{Mbid}}","recording_mbid":"f3bba4cd-8018-468b-902e-bc8f029593e5","recording_name":"First","artist_credit_name":"Artist"},
                     {"reference_mbid":"{{Mbid}}","recording_mbid":"{{Mbid}}","recording_name":"Seed","artist_credit_name":"Artist"},
                     {"reference_mbid":"bad","recording_mbid":"8e74dd9d-e5a3-4acd-918a-c36a0f8cda84","recording_name":"Wrong seed","artist_credit_name":"Artist"},
                     {"reference_mbid":"{{Mbid}}","recording_mbid":"8e74dd9d-e5a3-4acd-918a-c36a0f8cda84","recording_name":"Second","artist_credit_name":"Artist"}]
                    """)
            };
        }
    }
}
