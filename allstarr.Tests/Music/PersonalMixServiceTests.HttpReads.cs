using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    private sealed class CountingHttp : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task Generation_counts_http_fanout_through_real_song_relationship_enrichment()
    {
        await Seed(count: 60, plays: 10);
        await using (var db = factory.CreateDbContext())
            await db.ListeningEvents.ExecuteUpdateAsync(update => update.SetProperty(item => item.CanonicalRecordingId, (Guid?)null));
        var chart = EnableChart();
        using var handler = new CountingHttp();
        using var http = new HttpClient(handler);
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.SetupGet(x => x.ProviderId).Returns("deezer");
        metadata.SetupGet(x => x.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(x => x.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderTrackLookupRequest request) =>
            {
                using var response = await http.GetAsync("https://fixture.invalid/track/" + request.Id.Value, context.CancellationToken);
                var song = songs[request.Id.Value];
                return ProviderOutcome<ProviderTrackMetadata>.Success(new(request.Id, song.Title,
                    Enumerable.Range(0, 10).Select(i => new ProviderArtistCredit(i == 0 ? song.Artist : "Guest " + request.Id.Value + ":" + i)).ToArray(), albumTitle: "Album"));
            });
        metadata.Setup(x => x.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderMetadataSearchRequest request) =>
            {
                using var response = await http.GetAsync("https://fixture.invalid/artist/" + Uri.EscapeDataString(request.Query), context.CancellationToken);
                return ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer", []));
            });
        metadata.Setup(x => x.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderMetadataSearchRequest _) =>
            {
                using var response = await http.GetAsync("https://fixture.invalid/album", context.CancellationToken);
                return ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer", []));
            });
        var descriptor = new ProviderDescriptor("deezer", "Deezer", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack"])], new());
        var registry = new ProviderRegistry([new ProviderRegistration(descriptor, [metadata.Object])]);
        var router = new Mock<IProviderRouter>();
        router.Setup(x => x.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderMetadataCapability>(request,
                [new(0, descriptor, descriptor.Capabilities[0], metadata.Object,
                    new(request.Actor, "deezer", null, request.Policy, request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken), null)],
                new(request.CorrelationId, request.Capability, "deezer", null, [])));
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(http);
        var real = new ProtocolProviderGateway(router.Object, registry, clients.Object);
        gateway.Setup(x => x.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>())).ReturnsAsync((allstarr.Models.Domain.Song?)null);
        gateway.Setup(x => x.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns(async (ProtocolExecutionContext context, string provider, string id) =>
            {
                var song = await real.GetSongAsync(context, provider, id);
                if (song != null) song.CanonicalRecordingId = songs[id].CanonicalRecordingId;
                return song;
            });
        gateway.Setup(x => x.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), It.IsAny<int>()))
            .Returns(async (ProtocolExecutionContext context, string _, string query, int _) =>
            {
                using var response = await http.GetAsync("https://fixture.invalid/search/" + Uri.EscapeDataString(query), context.CancellationToken);
                return ProviderOutcome<IReadOnlyList<allstarr.Models.Domain.Song>>.Success([]);
            });
        gateway.Setup(x => x.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 0, 0, 5, "deezer"))
            .Returns(async (ProtocolExecutionContext context, string query, int _, int _, int _, string? _) =>
            {
                using var response = await http.GetAsync("https://fixture.invalid/artists/" + Uri.EscapeDataString(query), context.CancellationToken);
                return new allstarr.Models.Search.SearchResult { Artists = [new() { Name = query, ExternalId = query }] };
            });
        gateway.Setup(x => x.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 50))
            .Returns(async (ProtocolExecutionContext context, string _, string artist, int _) =>
            {
                using var response = await http.GetAsync("https://fixture.invalid/related/" + Uri.EscapeDataString(artist), context.CancellationToken);
                return new List<allstarr.Models.Domain.Artist>();
            });
        gateway.Setup(x => x.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns(async (ProtocolExecutionContext context, string _, string artist) =>
            {
                using var response = await http.GetAsync("https://fixture.invalid/releases/" + Uri.EscapeDataString(artist), context.CancellationToken);
                return new List<allstarr.Models.Domain.Album>();
            });
        var result = await service.GenerateAsync(owner, scheduled: true);
        Assert.Equal(240, result[0].Evidence!.ProviderReads);
        // Finding your own songs has its own allowance, separate from each mix's search for new ones.
        Assert.Equal(60, gateway.Invocations.Count(x => x.Method.Name == "GetSongAsync"));
        Assert.Equal(900, handler.Calls + chart.Reads);
        metadata.Verify(x => x.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Exactly(600));
        metadata.Verify(x => x.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Exactly(60));
        var calls = handler.Calls + chart.Reads;
        await service.GenerateAsync(owner, scheduled: true);
        Assert.Equal(calls, handler.Calls + chart.Reads);
        output.WriteLine($"Generation: 240 reads, {calls} HTTP calls (60 song reads × 12 calls, 180 other reads). Same-day repeat: 0 HTTP calls.");
    }
}
