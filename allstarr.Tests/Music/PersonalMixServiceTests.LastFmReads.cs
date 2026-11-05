using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Models.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Theory]
    [InlineData("http", ProviderErrorKind.TransientFailure)]
    [InlineData("rate", ProviderErrorKind.RateLimited)]
    [InlineData("timeout", ProviderErrorKind.TransientFailure)]
    [InlineData("malformed", ProviderErrorKind.CapabilityUnavailable)]
    [InlineData("partial", ProviderErrorKind.CapabilityUnavailable)]
    public async Task SimilarReadErrorsPreserveTheStoredDiscoveryMixAndItsReadMarker(string mode, ProviderErrorKind expected)
    {
        await Seed(count: 1, plays: 10);
        await NewPicks();
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "discover-weekly").Id;
        var before = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);
        Assert.NotEmpty(before.Songs);
        var readAt = await DiscoveryReadAt();
        clock.UtcNow += TimeSpan.FromMinutes(16);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 0, 0, 5, "deezer"))
            .ReturnsAsync(new SearchResult());
        using var handler = new SimilarReadHandler(mode);
        service = WithSimilarReads(handler);

        var error = await Assert.ThrowsAsync<PlaylistSourceUnavailableException>(() => service.GenerateAsync(owner, id));

        Assert.Equal("lastfm", error.ProviderId);
        Assert.Equal(expected, error.Error!.Kind);
        if (mode == "rate") Assert.Equal(TimeSpan.FromSeconds(17), error.Error.RetryAfter);
        Assert.Equal(1, handler.Reads);
        var after = await service.GetAsync(owner, id);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Songs.Select(item => item.Song.Id), after.Songs.Select(item => item.Song.Id));
        Assert.Equal(readAt, await DiscoveryReadAt());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptySuggestionsAndMissingOptionalAccountsStillCompleteDiscovery(bool connected)
    {
        await Seed(count: 1, plays: 10);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 0, 0, 5, "deezer"))
            .ReturnsAsync(new SearchResult());
        using var handler = new SimilarReadHandler("empty");
        service = WithSimilarReads(handler, connected);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "discover-weekly").Id;

        var result = (await service.GenerateAsync(owner, id)).Single(item => item.Id == id);

        Assert.Equal(1, result.Generation);
        Assert.Empty(result.Songs);
        Assert.Equal(connected ? 1 : 0, handler.Reads);
        Assert.NotNull(await DiscoveryReadAt());
    }

    [Theory]
    [InlineData("rate", "rate-limited")]
    [InlineData("http", "transient-failure")]
    [InlineData("timeout", "transient-failure")]
    public async Task SimilarReadJobErrorsKeepTheirRetryClassification(string mode, string code)
    {
        await Seed(count: 1, plays: 10);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 0, 0, 5, "deezer"))
            .ReturnsAsync(new SearchResult());
        using var handler = new SimilarReadHandler(mode);
        service = WithSimilarReads(handler);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "discover-weekly").Id;
        var jobs = new DurableJobQueue(factory, new(), new(new()), clock);
        await jobs.EnqueueAsync(new DurableJobEnqueueRequest<Guid>(PersonalMixService.JobType, "mix-refresh:similar-read", id, owner));
        var claim = (await jobs.ClaimNextAsync("mix-worker", [PersonalMixService.JobType]))!;
        using var services = new ServiceCollection().BuildServiceProvider();

        var completion = await new PersonalMixJobHandler(service).ExecuteAsync(new(claim, services), default);

        Assert.Equal(DurableJobCompletionKind.Retry, completion.Kind);
        Assert.Equal("mix-source-read-" + code, completion.ErrorCode);
        if (mode == "rate") Assert.Equal(TimeSpan.FromSeconds(17), completion.RetryDelay);
        Assert.Equal(1, handler.Reads);
        Assert.Null(await DiscoveryReadAt());
        Assert.Equal(0, (await service.GetAsync(owner, id)).Generation);

        await jobs.CompleteAsync(claim, completion);
        clock.UtcNow += TimeSpan.FromHours(1);
        var retry = (await jobs.ClaimNextAsync("mix-worker", [PersonalMixService.JobType]))!;
        Assert.Equal(2, retry.AttemptNumber);
        using var recovered = new SimilarReadHandler("empty");
        service = WithSimilarReads(recovered);

        var completed = await new PersonalMixJobHandler(service).ExecuteAsync(new(retry, services), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completed.Kind);
        Assert.Equal(1, recovered.Reads);
        Assert.NotNull(await DiscoveryReadAt());
        Assert.Equal(1, (await service.GetAsync(owner, id)).Generation);
    }

    private PersonalMixService WithSimilarReads(SimilarReadHandler handler, bool connected = true)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(new HttpClient(handler));
        var absent = new Mock<IScopedRecommendationAccountAccessor>();
        absent.Setup(item => item.HasAccountAsync(It.IsAny<IntelligenceScope>(), "lastfm", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var similar = new LastFmSimilarTracks(clients.Object, connected ? new ChartAccounts(owner) : absent.Object,
            NullLogger<LastFmSimilarTracks>.Instance);
        return new(factory, clock, tags, taste, new MusicMixService(gateway.Object, timeProvider: clock), gateway.Object,
            NewReleaseService(), settings.Object, policies.Object, access.Object, similarTracks: similar);
    }

    private async Task<DateTimeOffset?> DiscoveryReadAt()
    {
        await using var db = factory.CreateDbContext();
        var row = await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "personal-mixes" && item.OwnerUserId == owner)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync();
        if (row == null) return null;
        using var document = JsonDocument.Parse(row.PayloadJson);
        return document.RootElement.GetProperty("FillReadAt").TryGetProperty("discover-weekly", out var readAt)
            ? readAt.GetDateTimeOffset() : null;
    }

    private sealed class SimilarReadHandler(string mode) : HttpMessageHandler
    {
        internal int Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Reads++;
            if (mode == "timeout") throw new OperationCanceledException();
            var response = new HttpResponseMessage(mode == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(mode switch
                {
                    "rate" => "{\"error\":29}",
                    "malformed" => "not json",
                    "partial" => "{\"similartracks\":{\"track\":[{\"name\":\"Valid\",\"artist\":{\"name\":\"Artist\"}},{\"name\":\"Incomplete\"}]}}",
                    _ => "{\"similartracks\":{\"track\":[]}}"
                })
            };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        }
    }
}
