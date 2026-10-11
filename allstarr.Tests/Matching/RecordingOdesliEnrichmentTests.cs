using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
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
                PayloadJson = "{\"Title\":\"Source title\",\"Artist\":\"Source artist\"}",
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
        var settings = Options.Create(new OdesliSettings());
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        var queue = new RecordingOdesliEnrichmentQueue(jobs, settings, _clock, NullLogger<RecordingOdesliEnrichmentQueue>.Instance);
        await queue.EnqueueAsync(_userA, sourceId, "disabled", default);
        await using (var db = await _factory.CreateDbContextAsync()) Assert.Empty(await db.Jobs.ToListAsync());
        settings.Value.Enabled = true;
        var http = new CrossLinkHandler(status);
        var registry = new Mock<IProviderRegistry>();
        registry.SetupGet(item => item.Providers).Returns([
            new("qobuz", "Qobuz", "Fixture", ProviderOrigin.BuiltIn, "1", "1", [], new())
        ]);
        var commands = new TrackMatchCommandService(_factory, new(), new ProviderAccountResolver(_factory), _clock,
            new TestBackendLibraryAccess(_factory, "music"), crossLinks: queue);
        var initial = await commands.RematchSnapshotAsync(new TrackMatchActor(_userA, false), snapshotId, "initial");
        Assert.True(initial.Succeeded);
        Assert.Equal("unresolved", initial.State);
        Assert.Equal(0, http.Calls);
        var claim = await jobs.ClaimNextAsync("cross-links", [RecordingOdesliEnrichmentQueue.JobType]);
        Assert.NotNull(claim);
        var handler = new RecordingOdesliEnrichmentJobHandler(_factory, OdesliServiceTests.Create(http), settings,
            _service, registry.Object, _clock, jobs);
        var denied = await handler.ExecuteAsync(new(claim with { OwnerUserId = _userB }, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Failed, denied.Kind);
        Assert.Equal(0, http.Calls);
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
            Assert.Equal(1, http.Calls);
            return;
        }
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(1, http.Calls);
        var replay = await handler.ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, replay.Kind);
        Assert.Equal(1, http.Calls);
        var result = await commands.RematchSnapshotAsync(new TrackMatchActor(_userA, false), snapshotId, "with-evidence");
        Assert.True(result.Succeeded);
        Assert.Equal("accepted", result.State);
        await using var verify = await _factory.CreateDbContextAsync();
        var rematch = await verify.Jobs.SingleAsync(item => item.Type == TrackRematchJobHandler.Type);
        Assert.Equal(_userA, rematch.OwnerUserId);
        var alias = await verify.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "qobuz");
        Assert.Equal(recording, alias.CanonicalRecordingId);
        Assert.Equal(ProviderIdentityScope.Catalog, alias.Scope);
        Assert.Equal("odesli-link", alias.VerificationMethod);
        Assert.Equal(localId, (await verify.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync()).LibraryTrackId);
        Assert.Equal(1, http.Calls);
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

    private sealed class CrossLinkHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""
                    {"linksByPlatform":{"tidal":{"url":"https://tidal.com/browse/track/42"},
                    "qobuz":{"url":"https://open.qobuz.com/track/99"}}}
                    """)
            });
        }
    }
}
