using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class LibraryArtistReviewJobTests : IAsyncLifetime
{
    private readonly Guid _firstAdmin = Guid.CreateVersion7(), _secondAdmin = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private readonly Clock _clock = new();
    private DurableJobQueue _jobs = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = _factory.CreateDbContext();
        foreach (var id in new[] { _firstAdmin, _secondAdmin })
            db.Users.Add(new()
            {
                Id = id,
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = id.ToString("N"),
                DisplayName = "Administrator",
                IsAdmin = true,
                Enabled = true,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                LastSeenAt = _clock.UtcNow
            });
        await db.SaveChangesAsync();
        var options = new DurableJobOptions();
        _jobs = new(_factory, options, new JobPayloadPolicy(options), _clock);
    }

    [Fact]
    public async Task ConcurrentAdministratorsQueueOnlyOneHouseholdReview()
    {
        var requests = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(number => Handler().RequestAsync(number % 2 == 0 ? _firstAdmin : _secondAdmin, default)));

        Assert.Single(requests, result => result.Queued);
        Assert.Equal(7, requests.Count(result => result.Running));
        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task ActiveReviewBlocksAnotherAdministratorAcrossUtcDays()
    {
        Assert.True((await Handler().RequestAsync(_firstAdmin, default)).Queued);
        _clock.UtcNow = _clock.UtcNow.AddDays(1);

        Assert.Equal((false, true), await Handler().RequestAsync(_secondAdmin, default));
        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task FinishedReviewDeduplicatesAcrossAdministratorsUntilTheNextUtcDay()
    {
        await Handler().RequestAsync(_firstAdmin, default);
        var claim = (await _jobs.ClaimNextAsync("review", [LibraryArtistReviewJob.Type]))!;
        await _jobs.CompleteAsync(claim, DurableJobCompletion.Success());

        Assert.Equal((false, false), await Handler().RequestAsync(_secondAdmin, default));
        _clock.UtcNow = _clock.UtcNow.AddDays(1);
        Assert.Equal((true, false), await Handler().RequestAsync(_secondAdmin, default));
    }

    [Fact]
    public async Task RestartRetainsTheSavedArtistStartDelay()
    {
        await Handler().RequestAsync(_firstAdmin, default);
        var claim = (await _jobs.ClaimNextAsync("before-restart", [LibraryArtistReviewJob.Type]))!;
        var json = JsonSerializer.Serialize(new { LastId = "10", Completed = 1, NextAt = _clock.UtcNow.AddSeconds(3) });
        await using (var db = _factory.CreateDbContext())
        {
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _firstAdmin,
                SourceJobId = claim.JobId,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = _firstAdmin.ToString("N"),
                ProviderId = "allstarr",
                ResourceKind = "library-artist-review-checkpoint",
                ExternalIdHash = CanonicalCatalogKeys.Hash(claim.JobId.ToString("D")),
                SnapshotVersion = 1,
                ProviderRevision = "checkpoint",
                PayloadJson = json,
                PayloadSha256 = CanonicalCatalogKeys.Hash(json),
                CorrelationId = claim.CorrelationId,
                RetrievedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        await _jobs.RecoverInterruptedAsync();
        var resumed = (await _jobs.ClaimNextAsync("after-restart", [LibraryArtistReviewJob.Type]))!;

        // The handler deliberately has no library/provider dependencies: the saved delay must win first.
        var result = await Handler().ExecuteAsync(new(resumed, Mock.Of<IServiceProvider>()), default);

        Assert.True(result.IsContinuation);
        Assert.Equal(TimeSpan.FromSeconds(2), result.RetryDelay);
    }

    [Fact]
    public async Task SavedPayloadCannotActForAnotherAdministrator()
    {
        await Handler().RequestAsync(_firstAdmin, default);
        var claim = (await _jobs.ClaimNextAsync("review", [LibraryArtistReviewJob.Type]))!;
        claim = claim with { Payload = JsonSerializer.SerializeToElement(new LibraryArtistReviewPayload(_secondAdmin)) };

        var result = await Handler().ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
        Assert.Equal("library_artist_review_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task ReviewPersistsUsableNamesakesQueuesLinksAndResumesOneArtistAfterThreeSeconds()
    {
        var access = new TestBackendLibraryAccess(_factory, "music");
        var gateway = Mock.Of<IProtocolProviderGateway>();
        var empty = new EmptyServiceArtists();
        for (var number = 0; number < 10; number++) empty.Record("deezer", $"empty-{number}", 0);
        var searches = new List<(string Provider, string Query)>();
        var descriptors = new List<ProviderDescriptor>();
        var implementations = new Dictionary<string, IProviderMetadataCapability>();
        foreach (var provider in new[] { "qobuz", "deezer" })
        {
            var capability = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata,
                ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1.0", ["searchArtists"]);
            descriptors.Add(new(provider, provider, "Fixture metadata", ProviderOrigin.BuiltIn, "1", "1.0",
                [capability], new ProviderPermissionDescriptor()));
            var metadata = new Mock<IProviderMetadataCapability>();
            metadata.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .ReturnsAsync((ProviderExecutionContext context, ProviderMetadataSearchRequest request) =>
                {
                    searches.Add((context.ProviderId, request.Query));
                    return ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new(context.ProviderId,
                    [
                        new(new(context.ProviderId, ProviderResourceKind.Artist, request.Query.ToLowerInvariant()), request.Query),
                        new(new(context.ProviderId, ProviderResourceKind.Artist, "unrelated"), "Someone Else")
                    ]));
                });
            implementations[provider] = metadata.Object;
        }
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.FindByCapability(ProviderCapabilityKind.Metadata, false)).Returns(descriptors);
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderMetadataCapability>(request,
                descriptors.Select((descriptor, priority) => new ProviderRouteCandidate<IProviderMetadataCapability>(priority,
                    descriptor, descriptor.Capabilities.Single(), implementations[descriptor.Id],
                    new(request.Actor, descriptor.Id, null, request.Policy, request.OperationId, request.CorrelationId,
                        request.Deadline, request.CancellationToken), null)).ToArray(),
                new(request.CorrelationId, ProviderCapabilityKind.Metadata, "qobuz", null, [])));
        var index = new LibraryArtistIndex(_factory, access, _clock,
            new TrackMatchPlayableSearch(gateway, new(), NullLogger<TrackMatchPlayableSearch>.Instance), gateway,
            Mock.Of<ITrackIdentityService>(), registry.Object, empty);
        var permissions = new Mock<IBackendLibraryPermissionSource>();
        permissions.Setup(item => item.CreateRequestAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://fixture/Users/admin/Items"));
        using var http = new ArtistLibraryHttp();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(http, disposeHandler: false));
        using var cache = new MemoryApplicationCache(_clock);
        var review = new LibraryArtistReview(index, registry.Object, access, permissions.Object, clients.Object, cache);
        var links = new LibraryArtistLinks(index, _jobs, _clock);
        LibraryArtistReviewJob RestartedHandler() => new(_factory, _jobs, _clock, access, review, index, links,
            registry.Object, router.Object, NullLogger<LibraryArtistReviewJob>.Instance);
        await RestartedHandler().RequestAsync(_firstAdmin, default);
        var claim = (await _jobs.ClaimNextAsync("review-first", [LibraryArtistReviewJob.Type]))!;

        var first = await RestartedHandler().ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);

        Assert.True(first.IsContinuation);
        Assert.Equal(["FLO", "FLO"], searches.Select(search => search.Query));
        var candidates = (await index.ReadReviewAsync(_firstAdmin, ["FLO"]))["FLO"].Candidates;
        var candidate = Assert.Single(candidates);
        Assert.Equal("qobuz", candidate.ProviderId);
        Assert.Equal("flo", candidate.ArtistId);
        await using (var db = _factory.CreateDbContext())
        {
            var link = Assert.Single(await db.Jobs.Where(job => job.Type == LibraryArtistLinks.JobType).ToArrayAsync());
            Assert.Equal("FLO", JsonSerializer.Deserialize<LibraryArtistLinkPayload>(link.PayloadJson)!.Name);
            var stored = Assert.Single(await db.ExternalMetadataSnapshots.Where(row => row.ResourceKind == "library-artist-candidate").ToArrayAsync());
            Assert.DoesNotContain("Someone Else", stored.PayloadJson);
            Assert.DoesNotContain("deezer", stored.PayloadJson);
        }
        var early = await RestartedHandler().ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.True(early.IsContinuation);
        Assert.Equal(TimeSpan.FromSeconds(3), early.RetryDelay);
        Assert.Equal(2, searches.Count);
        await _jobs.CompleteAsync(claim, first);
        Assert.Null(await _jobs.ClaimNextAsync("too-soon", [LibraryArtistReviewJob.Type]));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(3);
        var resumed = (await _jobs.ClaimNextAsync("review-second", [LibraryArtistReviewJob.Type]))!;

        var second = await RestartedHandler().ExecuteAsync(new(resumed, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, second.Kind);
        await _jobs.CompleteAsync(resumed, second);
        Assert.Equal(["FLO", "FLO", "Nia", "Nia"], searches.Select(search => search.Query));
        Assert.Equal("nia", Assert.Single((await index.ReadReviewAsync(_firstAdmin, ["Nia"]))["Nia"].Candidates).ArtistId);
        await using var saved = _factory.CreateDbContext();
        Assert.Equal(DurableJobState.Succeeded, (await saved.Jobs.SingleAsync(job => job.Type == LibraryArtistReviewJob.Type)).State);
        Assert.Equal(2, await saved.Jobs.CountAsync(job => job.Type == LibraryArtistLinks.JobType));
        Assert.Equal(1, http.Requests);
    }

    private sealed class ArtistLibraryHttp : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.EndsWith("/Artists", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Items":[{"Id":"02","Name":"Nia","SongCount":2},{"Id":"01","Name":"FLO","SongCount":3}]}""")
            });
        }
    }

    private LibraryArtistReviewJob Handler() => new(_factory, _jobs, _clock, new TestBackendLibraryAccess(_factory, "music"),
        null!, null!, null!, null!, null!, NullLogger<LibraryArtistReviewJob>.Instance);

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
