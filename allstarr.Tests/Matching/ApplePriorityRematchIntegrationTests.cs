using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ApplePriorityRematchIntegrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("USRC17607839")]
    [Trait("Category", "Sqlite")]
    public async Task Force_rematch_selects_Apple_over_cached_Deezer_and_keeps_both_routes(string? isrc)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var now = DateTimeOffset.UtcNow;
        var clock = new Clock(now);
        var userId = Guid.CreateVersion7();
        var spotifyAccountId = Guid.CreateVersion7();
        var appleAccountId = Guid.CreateVersion7();
        var recordingId = Guid.CreateVersion7();
        var sourceIdentity = Identity(recordingId, "spotify", "source-track", now);
        sourceIdentity.Scope = ProviderIdentityScope.Account;
        sourceIdentity.ProviderAccountId = spotifyAccountId;
        var deezerIdentity = Identity(recordingId, "deezer", "deezer-track", now);
        var snapshotId = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = userId,
                DisplayName = "Owner",
                Enabled = true,
                IsAdmin = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = userId.ToString("N"),
                CreatedAt = now,
                UpdatedAt = now
            });
            db.ProviderAccounts.AddRange(
                Account(spotifyAccountId, userId, "spotify", now),
                Account(appleAccountId, userId, "apple-musickit", now));
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = recordingId,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.ProviderTrackIdentities.AddRange(sourceIdentity, deezerIdentity);
            db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = snapshotId,
                OwnerUserId = userId,
                ProviderAccountId = spotifyAccountId,
                ProviderTrackIdentityId = sourceIdentity.Id,
                ProviderId = "spotify",
                ResourceKind = "track",
                BackendInstanceId = "backend",
                BackendPrincipalId = userId.ToString("N"),
                Protocol = "jellyfin",
                ExternalIdHash = sourceIdentity.ExternalIdHash,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    Title = "Track",
                    Artist = "Artist",
                    Album = "Album",
                    DurationMilliseconds = 180_000,
                    Isrc = isrc
                }),
                PayloadSha256 = Hash("snapshot"),
                CorrelationId = "seed",
                RetrievedAt = now
            });
            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = userId,
                ExternalSnapshotId = snapshotId,
                CanonicalRecordingId = recordingId,
                State = TrackMatchState.Accepted,
                Confidence = .99,
                Threshold = .88,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
                PolicyVersion = "previous",
                CandidateResultsJson = "[]",
                ReasonsJson = "[]",
                WarningsJson = "[]",
                CorrelationId = "seed",
                DecidedAt = now
            });
            await db.SaveChangesAsync();
        }

        var storage = new DurableStorageState(database.StorageOptions);
        storage.Set(DurableStorageReadiness.Ready, "fixture");
        var identities = new TrackIdentityService(factory, storage, clock);
        var appleMetadata = Metadata("apple-musickit", "apple-track", isrc);
        var deezerMetadata = Metadata("deezer", "deezer-track", isrc);
        var registry = new ProviderRegistry([
            Registration(appleMetadata.Object, requiresAccount: true),
            Registration(deezerMetadata.Object, requiresAccount: false)
        ]);
        var account = new ProviderAccountContext(appleAccountId, "apple-musickit",
            ProviderAccountScope.Personal, 1, ownerUserId: userId);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(
                It.Is<ProviderRouteAccountRequest>(request => request.ProviderId == "apple-musickit" &&
                    request.Actor.EffectiveUserId == userId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderRouteAccountResolution(account, 1));
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, accounts.Object, health.Object,
            Mock.Of<IProviderRouteSidecarSource>(MockBehavior.Strict), identities);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:StreamingOrder"] = "apple-download,deezer",
            ["Providers:MetadataOrder"] = "deezer,apple-musickit"
        }).Build();
        var gateway = new ProtocolProviderGateway(router, registry,
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict), configuration, identities: identities, audioVerifier: FixtureAudioVerifier.Instance);
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", userId.ToString("N"),
            new AllstarrPrincipal(userId, "jellyfin", "backend", userId.ToString("N"), "Owner", true),
            "apple-priority", now.AddMinutes(5), default);
        var providerOrder = gateway.GetProviderOrder(ProviderCapabilityKind.Streaming);
        Assert.Equal(new[] { "apple-musickit", "deezer" }, providerOrder);

        var songs = await gateway.SearchPlayableSongsAsync(protocol, "Track Artist", 10);
        Assert.Equal(2, songs.Count);
        Assert.Contains(songs, song => song.ExternalProvider == "apple-download" && song.ExternalId == "apple-track");
        Assert.Contains(songs, song => song.ExternalProvider == "deezer" && song.ExternalId == "deezer-track");

        var matcher = new TrackMatchDecisionEngine();
        var playableSearch = new TrackMatchPlayableSearch(gateway, matcher, NullLogger<TrackMatchPlayableSearch>.Instance);
        var playable = await playableSearch.MatchAsync(protocol,
            new(snapshotId.ToString("N"), "spotify", "source-track", "Track", "Artist", "Album", null,
                180_000, isrc, null, null),
            new(userId, "backend", spotifyAccountId, 2, 1, new HashSet<string>()), [], null, default);
        Assert.Equal(TrackMatchReviewState.Accepted, playable.Decision.State);
        Assert.Equal("apple-download", playable.SelectedExternal?.ExternalProvider);
        Assert.Equal(2, playable.RoutableExternalCandidates.Count);

        var commands = new TrackMatchCommandService(factory, matcher, new ProviderAccountResolver(factory),
            clock, new TestBackendLibraryAccess(factory, "music"), playableSearch,
            providerGateway: gateway, registry: registry);
        var jobOptions = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, jobOptions, new JobPayloadPolicy(jobOptions), clock,
            new DurableJobContextAuthorizer(factory));
        var rematches = new TrackRematchAllService(factory, queue, clock, registry);
        var preview = await rematches.PreviewAsync(new(userId, true), userId);
        Assert.Equal(1, preview.AutomaticDecisionsToReplace);
        var receipt = await rematches.QueueForceAsync(new(userId, true), preview);
        TrackRematchAllJobPayload payload;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == receipt.JobId);
            payload = JsonSerializer.Deserialize<TrackRematchAllJobPayload>(job.PayloadJson)!;
        }
        Assert.True(payload.Force);
        var claim = new DurableJobClaim(receipt.JobId, 1, TrackRematchJobHandler.Type,
            JsonSerializer.SerializeToElement(payload), userId, null, null,
            JsonSerializer.SerializeToElement(new { }), TrackRematchAllService.OperationCorrelation(payload.OperationId));
        var handler = new TrackRematchAllJobHandler(factory, rematches, commands, clock);
        var completion = await handler.ExecuteAsync(new DurableJobExecutionContext(claim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);

        await using var verify = await factory.CreateDbContextAsync();
        var decisions = await verify.TrackMatches.OrderBy(item => item.DecisionVersion).ToArrayAsync();
        Assert.Equal(2, decisions.Length);
        var decision = decisions[1];
        Assert.Equal(TrackMatchState.Accepted, decision.State);
        Assert.Equal("bulk-rematch-v1", decision.PolicyVersion);
        Assert.Equal(recordingId, decision.CanonicalRecordingId);
        var candidates = JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(decision.CandidateResultsJson)!;
        Assert.Equal(2, candidates.Length);
        Assert.Equal("apple-track", candidates[0].ProviderTrackIds!["apple-download"]);
        Assert.Equal("deezer-track", candidates[1].ProviderTrackIds!["deezer"]);
        var stored = await verify.ProviderTrackIdentities.ToArrayAsync();
        var routes = DurableProviderRouteSelector.Select(sourceIdentity, stored, providerOrder);
        Assert.Equal(new[] { "apple-musickit", "deezer" }, routes.Select(item => item.ProviderId));
        Assert.Equal(new[] { "apple-track", "deezer-track" }, routes.Select(item => item.ExternalId));
        var appleIdentity = Assert.Single(stored, item => item.ProviderId == "apple-musickit");
        Assert.Equal(ProviderIdentityScope.Catalog, appleIdentity.Scope);
        Assert.Equal(ProviderIdentityVerification.Verified, appleIdentity.Verification);
        Assert.Equal(recordingId, appleIdentity.CanonicalRecordingId);
        Assert.Equal("apple-track", appleIdentity.ExternalId);
        Assert.DoesNotContain(stored, item => item.ProviderId == "apple-download");
        var plan = await router.PlanAsync<IProviderStreamingCapability>(new(
            ProviderCapabilityKind.Streaming, protocol.RequireActor(),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, true, false),
            "rematch-playback", "apple-priority", now.AddMinutes(5), providerOrder,
            providerOrder.Select(provider => new ProviderRouteProviderState(provider,
                availableQualities: [ProviderAudioQuality.Any])),
            new("allstarr", ProviderResourceKind.Track, recordingId.ToString("D"))));
        Assert.Equal("apple-musickit", plan.Decision.SelectedProviderId);
        Assert.Equal(appleAccountId, plan.Decision.SelectedProviderAccountId);
        Assert.Equal(new[] { "apple-musickit", "deezer" }, plan.Candidates.Select(item => item.Provider.Id));
        Assert.Equal(new[] { "apple-track", "deezer-track" }, plan.Candidates.Select(item => item.TrackId!.Value));
        appleMetadata.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderMetadataSearchRequest>()), Times.AtLeastOnce);
        accounts.VerifyAll();
    }

    private static Mock<IProviderMetadataCapability> Metadata(string provider, string id, string? isrc)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(item => item.ProviderId).Returns(provider);
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(provider,
                [new(new(provider, ProviderResourceKind.Track, id), "Track", [new("Artist")],
                    albumTitle: "Album", duration: TimeSpan.FromSeconds(180), isrc: isrc)])));
        return metadata;
    }

    private static ProviderRegistration Registration(IProviderMetadataCapability metadata, bool requiresAccount)
    {
        var streaming = Mock.Of<IProviderStreamingCapability>(item => item.ProviderId == metadata.ProviderId &&
            item.Capability == ProviderCapabilityKind.Streaming, MockBehavior.Strict);
        return new(new(metadata.ProviderId, metadata.ProviderId, "Fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack"]),
             new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                requiresAccount ? ProviderAccountRequirement.Required : ProviderAccountRequirement.None,
                "1", ["getStreamLease"], requiresAccount ? [ProviderAccountScope.Personal] : [])], new()),
            [metadata, streaming], metadata.ProviderId == "apple-musickit" ? ["apple-download", "applemusic"] : []);
    }

    private static ProviderAccountRecord Account(Guid id, Guid user, string provider, DateTimeOffset now) => new()
    {
        Id = id,
        OwnerUserId = user,
        ProviderId = provider,
        DisplayName = provider,
        Enabled = true,
        Revision = 1,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static ProviderTrackIdentityRecord Identity(Guid recording, string provider, string id, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        CanonicalRecordingId = recording,
        ProviderId = provider,
        ResourceKind = ProviderResourceKind.Track,
        Scope = ProviderIdentityScope.Catalog,
        CatalogNamespace = "default",
        ExternalId = id,
        ExternalIdHash = Hash(id),
        Verification = ProviderIdentityVerification.Verified,
        VerificationMethod = "automatic-match",
        DecisionVersion = 1,
        VerifiedAt = now,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class Clock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
