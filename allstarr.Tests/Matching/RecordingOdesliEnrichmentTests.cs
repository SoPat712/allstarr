using System.Net;
using System.Collections.Immutable;
using allstarr.Core.Settings;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task DeezerIsrcHit_PersistsVerifiedCatalogIdentity()
    {
        const string isrc = "USRC17607839";
        var recording = await _service.CreateRecordingAsync(Actor(_userA), "Song", isrc);
        await Link(recording.Recording.Id, Context(Actor(_userA), "tidal"), "42");
        Guid sourceId;
        await using (var db = await _factory.CreateDbContextAsync())
            sourceId = (await db.ProviderTrackIdentities.SingleAsync()).Id;
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        await new RecordingOdesliEnrichmentQueue(jobs, _clock, NullLogger<RecordingOdesliEnrichmentQueue>.Instance)
            .EnqueueAsync(_userA, sourceId, "deezer-fixture", default);
        var claim = await jobs.ClaimNextAsync("fixture", [RecordingOdesliEnrichmentQueue.JobType]);
        Assert.NotNull(claim);
        var http = new DeezerIsrcHandler();
        var metadata = new DeezerProvider(new DeezerHttpClient(new HttpClient(http), 0));
        var resolver = CrossProviderLinkResolverTests.Create(metadata, ["deezer"]);
        var handler = new RecordingOdesliEnrichmentJobHandler(_factory, resolver, _service, _clock, jobs);

        var result = await handler.ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await using var verify = await _factory.CreateDbContextAsync();
        var identity = await verify.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "deezer");
        Assert.Equal("99", identity.ExternalId);
        Assert.Equal(recording.Recording.Id, identity.CanonicalRecordingId);
        Assert.Equal(ProviderIdentityVerification.Verified, identity.Verification);
        Assert.Equal("isrc-lookup", identity.VerificationMethod);
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task CrossServiceSuggestions_NeverBecomeIdentitiesOrTriggerRematching()
    {
        var recording = await _service.CreateRecordingAsync(Actor(_userA), "Song");
        await Link(recording.Recording.Id, Context(Actor(_userA), "tidal"), "42");
        Guid sourceId;
        await using (var db = await _factory.CreateDbContextAsync())
            sourceId = (await db.ProviderTrackIdentities.SingleAsync()).Id;
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        await new RecordingOdesliEnrichmentQueue(jobs, _clock, NullLogger<RecordingOdesliEnrichmentQueue>.Instance)
            .EnqueueAsync(_userA, sourceId, "suggestion-fixture", default);
        var claim = await jobs.ClaimNextAsync("fixture", [RecordingOdesliEnrichmentQueue.JobType]);
        Assert.NotNull(claim);
        var resolver = new Mock<ICrossProviderLinkResolver>();
        resolver.Setup(item => item.ResolveAsync(It.IsAny<ExternalTrackMatchSnapshot>(),
                It.IsAny<IReadOnlyList<ProviderExternalResourceId>>(), It.IsAny<ProviderActorContext>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new("deezer", "99", false, 0.75, "cross-service-match")]);
        var handler = new RecordingOdesliEnrichmentJobHandler(_factory, resolver.Object, _service, _clock, jobs);
        var result = await handler.ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Single(await verify.ProviderTrackIdentities.ToListAsync());
        Assert.False(await verify.Jobs.AnyAsync(item => item.Type == TrackRematchJobHandler.Type));
    }

    [Theory]
    [InlineData(ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod)]
    [InlineData(ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod)]
    public async Task ReleasedIdentities_AreNotPassedAsVerifiedEvidence(string method)
    {
        var recording = await _service.CreateRecordingAsync(Actor(_userA), "Song");
        await Link(recording.Recording.Id, Context(Actor(_userA), "tidal"), "42");
        await _service.LinkAsync(Context(Actor(_userA), "deezer"), new(recording.Recording.Id, Track("deezer", "99"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, method, 1));
        Guid sourceId;
        await using (var db = await _factory.CreateDbContextAsync())
            sourceId = (await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "tidal")).Id;
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        await new RecordingOdesliEnrichmentQueue(jobs, _clock, NullLogger<RecordingOdesliEnrichmentQueue>.Instance)
            .EnqueueAsync(_userA, sourceId, "released-fixture", default);
        var claim = await jobs.ClaimNextAsync("fixture", [RecordingOdesliEnrichmentQueue.JobType]);
        Assert.NotNull(claim);
        var resolver = new Mock<ICrossProviderLinkResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.ResolveAsync(It.IsAny<ExternalTrackMatchSnapshot>(),
                It.Is<IReadOnlyList<ProviderExternalResourceId>>(known => known.Count == 1 && known[0].ProviderId == "tidal"),
                It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var result = await new RecordingOdesliEnrichmentJobHandler(_factory, resolver.Object, _service, _clock, jobs)
            .ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        resolver.VerifyAll();
    }

    private sealed class DeezerIsrcHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https://api.deezer.com/track/isrc:USRC17607839", request.RequestUri!.AbsoluteUri);
            Assert.Empty(request.Headers);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":99,"title":"Song","isrc":"USRC17607839","duration":180,
                    "artist":{"id":1,"name":"Artist"},"album":{"id":2,"title":"Album"}}
                    """)
            });
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CrossLinks_RunAfterTheDecisionAndRematchOnlyTheOwnerUsingStoredEvidence(HttpStatusCode status)
    {
        var account = await SeedUserAccount("tidal", _userA);
        var recording = await _service.RememberSourceAsync(Context(Actor(_userA), "tidal", account), Track("tidal", "42"),
            "ext-tidal-song-42", ProviderIdentityScope.Account);
        Guid sourceId;
        var snapshotId = Guid.CreateVersion7();
        var localId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = await db.ProviderTrackIdentities.SingleAsync();
            sourceId = source.Id;
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = snapshotId,
                OwnerUserId = _userA,
                ProviderAccountId = account.Id,
                ProviderTrackIdentityId = source.Id,
                BackendInstanceId = "fixture",
                BackendPrincipalId = _userA.ToString("N"),
                Protocol = "jellyfin",
                ProviderId = "tidal",
                ResourceKind = "track",
                ExternalIdHash = source.ExternalIdHash,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadSha256 = source.ExternalIdHash,
                PayloadJson = "{\"Title\":\"Source title\",\"Artist\":\"Source artist\",\"DurationMilliseconds\":180000}",
                CorrelationId = "setup",
                RetrievedAt = _clock.UtcNow
            });
            db.LibraryTracks.Add(new()
            {
                Id = localId,
                OwnerUserId = _userA,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendLibraryId = "music",
                BackendItemId = "native",
                Title = "Alternate title",
                Artist = "Alternate artist",
                ProviderIdsJson = "{\"qobuz\":\"99\"}",
                IndexedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        var queue = new RecordingOdesliEnrichmentQueue(jobs, _clock, NullLogger<RecordingOdesliEnrichmentQueue>.Instance);
        var commands = new TrackMatchCommandService(_factory, new(), new ProviderAccountResolver(_factory), _clock,
            new TestBackendLibraryAccess(_factory, "music"), crossLinks: queue);
        var initial = await commands.RematchSnapshotAsync(new TrackMatchActor(_userA, false), snapshotId, "initial");
        Assert.True(initial.Succeeded);
        Assert.Equal("unresolved", initial.State);
        var claim = await jobs.ClaimNextAsync("cross-links", [RecordingOdesliEnrichmentQueue.JobType]);
        Assert.NotNull(claim);
        var resolver = new Mock<ICrossProviderLinkResolver>();
        var lookup = resolver.Setup(item => item.ResolveAsync(It.Is<ExternalTrackMatchSnapshot>(snapshot => snapshot.DurationMilliseconds == 180000),
            It.IsAny<IReadOnlyList<ProviderExternalResourceId>>(), It.IsAny<ProviderActorContext>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()));
        if (status == HttpStatusCode.OK) lookup.ReturnsAsync([new("qobuz", "99", true, 1, "cross-service-match")]);
        else lookup.ThrowsAsync(new OdesliLookupException(true, TimeSpan.FromMinutes(1)));
        var handler = new RecordingOdesliEnrichmentJobHandler(_factory, resolver.Object,
            _service, _clock, jobs);
        var denied = await handler.ExecuteAsync(new(claim with { OwnerUserId = _userB }, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Failed, denied.Kind);
        Assert.Empty(resolver.Invocations);
        var completion = await handler.ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        if (status != HttpStatusCode.OK)
        {
            Assert.Equal(DurableJobCompletionKind.Retry, completion.Kind);
            Assert.Equal(TimeSpan.FromMinutes(1), completion.RetryDelay);
            var stillUsable = await commands.RematchSnapshotAsync(new TrackMatchActor(_userA, false), snapshotId, "during-outage");
            Assert.True(stillUsable.Succeeded);
            Assert.Equal("unresolved", stillUsable.State);
            await using var unchanged = await _factory.CreateDbContextAsync();
            Assert.Single(await unchanged.ProviderTrackIdentities.ToListAsync());
            Assert.False(await unchanged.Jobs.AnyAsync(item => item.Type == TrackRematchJobHandler.Type));
            return;
        }
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        var replay = await handler.ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, replay.Kind);
        var result = await commands.RematchSnapshotAsync(new TrackMatchActor(_userA, false), snapshotId, "with-evidence");
        Assert.True(result.Succeeded);
        Assert.Equal("accepted", result.State);
        await using var verify = await _factory.CreateDbContextAsync();
        var rematch = await verify.Jobs.SingleAsync(item => item.Type == TrackRematchJobHandler.Type);
        Assert.Equal(_userA, rematch.OwnerUserId);
        var alias = await verify.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "qobuz");
        Assert.Equal(recording, alias.CanonicalRecordingId);
        Assert.Equal(ProviderIdentityScope.Catalog, alias.Scope);
        Assert.Equal("cross-service-match", alias.VerificationMethod);
        Assert.Equal(localId, (await verify.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync()).LibraryTrackId);
    }

    [Fact]
    public async Task CrossLinks_DoNotReplacePinnedOrConflictingAssociations()
    {
        var source = await _service.CreateRecordingAsync(Actor(_userA), "source");
        var other = await _service.CreateRecordingAsync(Actor(_userA), "other");
        var context = Context(Actor(_userA), "tidal");
        await Link(source.Recording.Id, context, "42");
        await _service.LinkAsync(Context(Actor(_userA), "qobuz"), new(other.Recording.Id, Track("qobuz", "99"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Pinned, "manual", 1));
        Assert.Equal(0, await _service.AddExactAliasesAsync(context, Track("tidal", "42"), [Track("qobuz", "99")]));
        await using var db = await _factory.CreateDbContextAsync();
        var pinned = await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "qobuz");
        Assert.Equal(other.Recording.Id, pinned.CanonicalRecordingId);
        Assert.Equal(ProviderIdentityVerification.Pinned, pinned.Verification);
        Assert.Equal(2, await db.CanonicalRecordings.CountAsync(item => item.MergedIntoId == null));
    }

    [Theory]
    [InlineData(ProviderIdentityVerification.Pinned, "manual")]
    [InlineData(ProviderIdentityVerification.Verified, "automatic-suggestion")]
    [InlineData(ProviderIdentityVerification.Verified, "superseded")]
    public async Task CrossLinks_CannotPromoteUnprovenSourceAssociations(ProviderIdentityVerification verification, string method)
    {
        var source = await _service.CreateRecordingAsync(Actor(_userA), "source");
        var context = Context(Actor(_userA), "tidal");
        await _service.LinkAsync(context, new(source.Recording.Id, Track("tidal", "42"),
            ProviderIdentityScope.Catalog, verification, method, 1));
        Assert.Equal(0, await _service.AddExactAliasesAsync(context, Track("tidal", "42"), [Track("qobuz", "99")]));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.ProviderTrackIdentities.ToListAsync());
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("different", false)]
    [InlineData("missing", false)]
    [InlineData("account", false)]
    [InlineData("failure", false)]
    [InlineData("exception", false)]
    public async Task IsrcCrossLinks_RequireExactPublicEvidenceAndQueueAnOwnerRematch(string outcome, bool addsLink)
    {
        const string isrc = "USRC17607839";
        var recording = await _service.CreateRecordingAsync(Actor(_userA), "isrc-source", isrc);
        var account = await SeedUserAccount("deezer", _userA);
        await Link(recording.Recording.Id, Context(Actor(_userA), "deezer", account), "42", ProviderIdentityScope.Account);
        Guid sourceId;
        var snapshotId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = await db.ProviderTrackIdentities.SingleAsync();
            sourceId = source.Id;
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = snapshotId,
                OwnerUserId = _userA,
                ProviderTrackIdentityId = sourceId,
                ProviderAccountId = account.Id,
                BackendInstanceId = "fixture",
                BackendPrincipalId = _userA.ToString("N"),
                Protocol = "jellyfin",
                ProviderId = "deezer",
                ResourceKind = "track",
                ExternalIdHash = source.ExternalIdHash,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadSha256 = source.ExternalIdHash,
                PayloadJson = "{}",
                CorrelationId = "isrc-fixture",
                RetrievedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        if (outcome != "account")
        {
            var lookup = metadata.Setup(item => item.LookupByIsrcAsync(
                It.Is<ProviderExecutionContext>(context => context.ProviderId == "qobuz" && context.Account == null &&
                    context.Actor.EffectiveUserId == _userA),
                It.Is<ProviderIsrcLookupRequest>(request => request.Isrc == isrc)));
            if (outcome == "exception") lookup.ThrowsAsync(new HttpRequestException("fixture"));
            else lookup.ReturnsAsync(outcome == "failure"
                ? ProviderOutcome<ProviderTrackMetadata>.Failure(new ProviderError(ProviderErrorKind.NotSupported))
                : ProviderOutcome<ProviderTrackMetadata>.Success(new(Track("qobuz", "99"), "Fixture", [new ProviderArtistCredit("Fixture artist")],
                    isrc: outcome == "missing" ? null : outcome == "different" ? "USRC17607840" : isrc)));
        }
        IProviderMetadataCapability? registered = metadata.Object;
        var registry = new Mock<IProviderRegistry>(MockBehavior.Strict);
        registry.SetupGet(item => item.Providers).Returns([
            new("qobuz", "Qobuz", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
                [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                    outcome == "account" ? ProviderAccountRequirement.Required : ProviderAccountRequirement.None, "1",
                    allowedAccountScopes: outcome == "account" ? [ProviderAccountScope.Personal] : [])], new())
        ]);
        if (outcome != "account") registry.Setup(item => item.TryGetCapability<IProviderMetadataCapability>(
            "qobuz", ProviderCapabilityKind.Metadata, out registered)).Returns(true);
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        await new RecordingOdesliEnrichmentQueue(jobs, _clock, NullLogger<RecordingOdesliEnrichmentQueue>.Instance)
            .EnqueueAsync(_userA, sourceId, "isrc-fixture", default);
        var claim = await jobs.ClaimNextAsync("isrc-fixture", [RecordingOdesliEnrichmentQueue.JobType]);
        Assert.NotNull(claim);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(_userA, It.IsAny<CancellationToken>())).ReturnsAsync(new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Metadata, ["qobuz"]),
            ImmutableHashSet<string>.Empty, "Any", 0.07));
        if (outcome == "failure")
            metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                    It.Is<ProviderMetadataSearchRequest>(request => request.Query == isrc)))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(new(ProviderErrorKind.NotFound)));
        var resolver = new CrossProviderLinkResolver(registry.Object, policies.Object, new(), new TestMemoryApplicationCache(),
            Options.Create(new OdesliSettings { Enabled = true }), _clock);
        var handler = new RecordingOdesliEnrichmentJobHandler(_factory, resolver, _service, _clock, jobs);

        var completion = await handler.ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await using var verify = await _factory.CreateDbContextAsync();
        var aliases = await verify.ProviderTrackIdentities.Where(item => item.ProviderId == "qobuz").ToListAsync();
        var rematches = await verify.Jobs.Where(item => item.Type == TrackRematchJobHandler.Type).ToListAsync();
        if (addsLink)
        {
            var alias = Assert.Single(aliases);
            Assert.Equal(recording.Recording.Id, alias.CanonicalRecordingId);
            Assert.Equal("isrc-lookup", alias.VerificationMethod);
            Assert.Equal(ProviderIdentityVerification.Verified, alias.Verification);
            Assert.Equal(ProviderIdentityScope.Catalog, alias.Scope);
            Assert.Equal(_userA, Assert.Single(rematches).OwnerUserId);
            var replay = await handler.ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, replay.Kind);
            Assert.Equal(1, await verify.Jobs.CountAsync(item => item.Type == TrackRematchJobHandler.Type));
        }
        else
        {
            Assert.Empty(aliases);
            Assert.Empty(rematches);
        }
        metadata.VerifyAll();
        metadata.VerifyNoOtherCalls();
        registry.VerifyAll();
        registry.VerifyNoOtherCalls();
    }

}
