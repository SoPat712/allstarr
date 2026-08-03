using System.Net;
using System.Net.Http.Headers;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Core.Intelligence;
using allstarr.Services.Common;
using allstarr.Services.MusicBrainz;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class MusicBrainzServiceTests
{
    [Fact]
    public async Task IsrcLookup_UsesNamedCurrentClientWithoutCredentialsAndCachesHits()
    {
        var handler = new QueueHandler(
            _ => Json("""{"recordings":[{"id":"31e68c1d-31f9-432c-a3a4-13aef4a53833","isrcs":["USABC1234567"]}]}"""));
        var factory = new RecordingFactory(handler);
        factory.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", "must-not-leave");
        var service = Create(factory);

        Assert.NotNull(await service.LookupByIsrcAsync("us-abc-12-34567"));
        Assert.NotNull(await service.LookupByIsrcAsync("USABC1234567"));

        Assert.Equal(MusicBrainzService.HttpClientName, factory.ClientName);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("/ws/2/isrc/USABC1234567", handler.RequestUris.Single().AbsolutePath);
        Assert.Null(handler.Authorizations.Single());
        Assert.Equal(MusicBrainzService.UserAgent, handler.UserAgents.Single());
    }

    [Fact]
    public async Task ResolveRecording_EscapesLuceneAndSelectsIdentityEvidenceInsteadOfFirstResult()
    {
        const string selectedId = "31e68c1d-31f9-432c-a3a4-13aef4a53833";
        var handler = new QueueHandler(
            _ => Json("""
                {"recordings":[
                  {"id":"41e68c1d-31f9-432c-a3a4-13aef4a53833","score":100,"title":"Wrong","length":180000,"artist-credit":[{"name":"Artist (US)"}]},
                  {"id":"31e68c1d-31f9-432c-a3a4-13aef4a53833","score":95,"title":"AC/DC: Live?","length":181000,"artist-credit":[{"name":"Artist (US)"}]}
                ]}
                """),
            request =>
            {
                Assert.EndsWith($"/recording/{selectedId}", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
                return Json($$"""{"id":"{{selectedId}}","title":"AC/DC: Live?","length":181000,"artist-credit":[{"name":"Artist (US)"}],"genres":[{"name":"Rock","count":4}]}""");
            });
        var service = Create(new RecordingFactory(handler));

        var match = await service.ResolveRecordingAsync(
            null, null, "AC/DC: Live?", "Artist (US)", 180000);

        Assert.NotNull(match);
        Assert.Equal(selectedId, match.Recording.Id);
        Assert.InRange(match.Confidence, .9, 1);
        var query = Uri.UnescapeDataString(handler.RequestUris[0].Query);
        Assert.Contains("recording:\"AC\\/DC\\: Live\\?\"", query, StringComparison.Ordinal);
        Assert.Contains("artist:\"Artist \\(US\\)\"", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup_RejectsOversizedResponsesAndHonorsRetryAfter()
    {
        var oversized = Json("{}");
        oversized.Content.Headers.ContentLength = MusicBrainzService.MaximumResponseBytes + 1;
        var oversizedError = await Assert.ThrowsAsync<MusicBrainzLookupException>(() =>
            Create(new RecordingFactory(new QueueHandler(_ => oversized)))
                .LookupByMbidAsync("31e68c1d-31f9-432c-a3a4-13aef4a53833"));
        Assert.False(oversizedError.Retryable);
        Assert.Equal("musicbrainz_response_too_large", oversizedError.Code);

        var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        var retryError = await Assert.ThrowsAsync<MusicBrainzLookupException>(() =>
            Create(new RecordingFactory(new QueueHandler(_ => unavailable)))
                .LookupByMbidAsync("41e68c1d-31f9-432c-a3a4-13aef4a53833"));
        Assert.True(retryError.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(7), retryError.RetryAfter);
    }

    [Fact]
    public async Task IdenticalLookupsAreCoalescedAndCancellationIsPropagated()
    {
        var handler = new QueueHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(50, cancellationToken);
            return Json("""{"recordings":[]}""");
        });
        var service = Create(new RecordingFactory(handler));

        await Task.WhenAll(
            service.LookupByIsrcAsync("USABC1234567"),
            service.LookupByIsrcAsync("USABC1234567"));
        Assert.Null(await service.LookupByIsrcAsync("USABC1234567"));
        Assert.Equal(1, handler.Calls);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.LookupByMbidAsync(
                "31e68c1d-31f9-432c-a3a4-13aef4a53833",
                new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("US-ABC-1")]
    public async Task InvalidIdentifiersAreRejectedBeforeNetworkUse(string value)
    {
        var handler = new QueueHandler(_ => throw new InvalidOperationException("must not request"));
        var service = Create(new RecordingFactory(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => service.LookupByMbidAsync(value));
        await Assert.ThrowsAsync<ArgumentException>(() => service.LookupByIsrcAsync(value));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void DurableResult_SurfacesConfidenceWithoutReplacingAcceptedMetadata()
    {
        var occurrence = new ListeningEventRecord
        {
            Title = "Accepted title",
            Artist = "Accepted artist",
            Album = "Accepted album"
        };
        var enrichedAt = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        MusicBrainzListeningEnrichmentJobHandler.ApplyResult(
            occurrence,
            new(new MusicBrainzRecording
            {
                Id = "31e68c1d-31f9-432c-a3a4-13aef4a53833",
                Title = "Remote title",
                Releases = [new() { Id = "41e68c1d-31f9-432c-a3a4-13aef4a53833", Title = "Remote album" }]
            }, .96, MusicBrainzService.SourceRevision),
            enrichedAt);

        Assert.Equal(MusicBrainzEnrichmentState.Resolved, occurrence.MusicBrainzEnrichmentState);
        Assert.Equal(.96, occurrence.MusicBrainzEnrichmentConfidence);
        Assert.Equal("31e68c1d-31f9-432c-a3a4-13aef4a53833", occurrence.RecordingMusicBrainzId);
        Assert.Contains("41e68c1d-31f9-432c-a3a4-13aef4a53833", occurrence.MusicBrainzFactsJson);
        Assert.Equal("Accepted title", occurrence.Title);
        Assert.Equal("Accepted artist", occurrence.Artist);
        Assert.Equal("Accepted album", occurrence.Album);

        MusicBrainzListeningEnrichmentJobHandler.ApplyResult(occurrence, null, enrichedAt.AddHours(1));
        Assert.Equal(MusicBrainzEnrichmentState.Unresolved, occurrence.MusicBrainzEnrichmentState);
        Assert.Null(occurrence.MusicBrainzEnrichmentConfidence);
        Assert.Null(occurrence.MusicBrainzFactsJson);
        Assert.Equal("Accepted title", occurrence.Title);
    }

    private static MusicBrainzService Create(RecordingFactory factory) => new(
        factory,
        Options.Create(new MusicBrainzSettings
        {
            Enabled = true,
            BaseUrl = "https://musicbrainz.test/ws/2",
            RateLimitMs = 1000
        }),
        Options.Create(new CacheSettings()),
        new TestMemoryApplicationCache(),
        new ApplicationCacheRequestCoalescer(new ApplicationCacheActivityMetrics()),
        NullLogger<MusicBrainzService>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient Client { get; } = new(handler);
        public string? ClientName { get; private set; }
        public HttpClient CreateClient(string name)
        {
            ClientName = name;
            return Client;
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses;
        public int Calls { get; private set; }
        public List<Uri> RequestUris { get; } = [];
        public List<AuthenticationHeaderValue?> Authorizations { get; } = [];
        public List<string> UserAgents { get; } = [];

        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : this(
            responses.Select<Func<HttpRequestMessage, HttpResponseMessage>, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(
                response => (request, _) => Task.FromResult(response(request))).ToArray())
        { }

        public QueueHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses) =>
            _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestUris.Add(request.RequestUri!);
            Authorizations.Add(request.Headers.Authorization);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return await _responses.Dequeue()(request, cancellationToken);
        }
    }
}
