using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Settings;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Theory]
    [InlineData(ProviderUseMode.ManualOnly)]
    [InlineData(ProviderUseMode.Off)]
    public async Task RecordingEvidenceJob_SkipsRestrictedSourceWithoutLookupOrWrites(ProviderUseMode mode)
    {
        var recording = await _service.CreateRecordingAsync(Actor(_userA), "restricted-source", "USRC17607839");
        await Link(recording.Recording.Id, Context(Actor(_userA), "spotify"), "restricted-source");
        var settings = Options.Create(new MusicBrainzSettings { Enabled = true });
        var client = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        client.SetupGet(item => item.ConfiguredSourceRevision).Returns("fixture:ws2");
        var policies = PolicyResolver(_userA, ("spotify", mode));
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        var queue = new RecordingMusicBrainzEnrichmentQueue(_factory, jobs, settings, client.Object, _clock);
        Assert.Equal(1, await queue.EnqueuePendingAsync(default));
        var claim = await jobs.ClaimNextAsync("recording-worker", [RecordingMusicBrainzEnrichmentQueue.JobType]);
        var handler = new RecordingMusicBrainzEnrichmentJobHandler(
            _factory, client.Object, settings, _service, policies.Object);

        var result = await handler.ExecuteAsync(new(claim!, Mock.Of<IServiceProvider>()), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        client.Verify(item => item.LookupByIsrcAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var service = new TrackIdentityService(_factory, _storageState, _clock, providerPolicies: policies.Object);
        var unchanged = await service.EnrichRecordingAsync(Actor(_userA), recording.Recording.Id,
            "USRC17607839", "31e68c1d-31f9-432c-a3a4-13aef4a53833", ["USRC17607839"], "restricted-source");
        Assert.Null(unchanged.MusicBrainzRecordingId);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.RecordingIdentifiers.ToListAsync());
        Assert.Empty(await db.AuditEvents.Where(item => item.Outcome == "enriched").ToArrayAsync());
    }

    [Theory]
    [InlineData(ProviderUseMode.ManualOnly, false)]
    [InlineData(ProviderUseMode.Off, false)]
    [InlineData(ProviderUseMode.ManualOnly, true)]
    [InlineData(ProviderUseMode.Off, true)]
    public async Task RecordingEvidence_DoesNotMergeRestrictedTarget(ProviderUseMode mode, bool targetHasMbid)
    {
        const string mbid = "31e68c1d-31f9-432c-a3a4-13aef4a53833";
        var source = await _service.CreateRecordingAsync(Actor(_userA), "eligible-source", "USRC17607839");
        var target = await _service.CreateRecordingAsync(Actor(_userA), "restricted-target", "GBAYE0601498", targetHasMbid ? mbid : null);
        await Link(source.Recording.Id, Context(Actor(_userA), "spotify"), "source-track");
        await Link(target.Recording.Id, Context(Actor(_userA), "qobuz"), "target-track");
        var policies = PolicyResolver(_userA, ("qobuz", mode));
        var service = new TrackIdentityService(_factory, _storageState, _clock,
            providerPolicies: policies.Object);

        var result = await service.EnrichRecordingAsync(Actor(_userA), source.Recording.Id,
            "USRC17607839", mbid, ["USRC17607839", "GBAYE0601498"], "restricted-target");

        Assert.Equal(source.Recording.Id, result.Id);
        Assert.Equal(targetHasMbid ? null : mbid, result.MusicBrainzRecordingId);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.RecordingIdentifiers.AnyAsync(item => item.RecordingId == source.Recording.Id && item.Value == "GBAYE0601498"));
        Assert.Null(await db.CanonicalRecordings.Where(item => item.Id == target.Recording.Id)
            .Select(item => item.MergedIntoId).SingleAsync());
        Assert.Equal(target.Recording.Id, (await db.ProviderTrackIdentities
            .SingleAsync(item => item.ProviderId == "qobuz")).CanonicalRecordingId);
    }

    [Fact]
    public async Task RecordingEvidence_MergesMultipleIsrcsIntoTheEarliestPublicIdAtomically()
    {
        const string mbid = "31e68c1d-31f9-432c-a3a4-13aef4a53833";
        var earlier = new TrackIdentityService(_factory, _storageState, new FakeClock(_clock.UtcNow.AddMinutes(-1)));
        var first = await earlier.CreateRecordingAsync(Actor(_userA), "first", "USRC17607839", null, "ext-deezer-first");
        var second = await _service.CreateRecordingAsync(Actor(_userB), "second", "GBAYE0601498", mbid, "ext-qobuz-song-second");
        await Link(second.Recording.Id, Context(Actor(_userB), "qobuz"), "second");
        var result = await _service.EnrichRecordingAsync(Actor(_userA), first.Recording.Id,
            "USRC17607839", mbid, ["USRC17607839", "GBAYE0601498"], "evidence");
        Assert.Equal(first.Recording.Id, result.Id);
        Assert.Equal("ext-deezer-first", result.PublicId);
        Assert.Equal(mbid, result.MusicBrainzRecordingId);
        Assert.Equal(result.Id, (await _service.ResolveRecordingAsync(Actor(_userB), second.Recording.PublicId))?.Id);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(result.Id, (await db.CanonicalRecordings.SingleAsync(item => item.Id == second.Recording.Id)).MergedIntoId);
        Assert.Equal(result.Id, (await db.ProviderTrackIdentities.SingleAsync()).CanonicalRecordingId);
        Assert.Equal(3, await db.RecordingIdentifiers.CountAsync());
        var auditCount = await db.AuditEvents.CountAsync();
        await _service.EnrichRecordingAsync(Actor(_userA), first.Recording.Id,
            "USRC17607839", mbid, ["USRC17607839", "GBAYE0601498"], "repeat");
        Assert.Equal(auditCount, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task RecordingEvidence_ConflictingMbidLeavesEveryRecordingAndIdentifierUnchanged()
    {
        const string existingMbid = "41e68c1d-31f9-432c-a3a4-13aef4a53833";
        var first = await _service.CreateRecordingAsync(Actor(_userA), "first", "USRC17607839");
        await _service.CreateRecordingAsync(Actor(_userA), "second", "GBAYE0601498", existingMbid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.EnrichRecordingAsync(Actor(_userA),
            first.Recording.Id, "USRC17607839", "31e68c1d-31f9-432c-a3a4-13aef4a53833",
            ["USRC17607839", "GBAYE0601498"], "conflict"));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.CanonicalRecordings.CountAsync(item => item.MergedIntoId == null));
        Assert.Equal(3, await db.RecordingIdentifiers.CountAsync());
        Assert.Equal(existingMbid, (await db.RecordingIdentifiers.SingleAsync(item => item.Kind == RecordingIdentifierKinds.MusicBrainz)).Value);
    }

    [Fact]
    public async Task RecordingEvidenceJob_IsOptionalDurableIdempotentAndWritesThroughTheOwner()
    {
        const string mbid = "31e68c1d-31f9-432c-a3a4-13aef4a53833";
        var recording = await _service.CreateRecordingAsync(Actor(_userA), "source", "USRC17607839");
        var settings = Options.Create(new MusicBrainzSettings());
        var client = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        client.SetupGet(item => item.ConfiguredSourceRevision).Returns("fixture:ws2");
        client.Setup(item => item.LookupByIsrcAsync("USRC17607839", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MusicBrainzRecording { Id = mbid, Isrcs = ["USRC17607839"] });
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        var queue = new RecordingMusicBrainzEnrichmentQueue(_factory, jobs, settings, client.Object, _clock);
        Assert.Equal(0, await queue.EnqueuePendingAsync(default));
        client.VerifyNoOtherCalls();
        settings.Value.Enabled = true;
        Assert.Equal(1, await queue.EnqueuePendingAsync(default));
        Assert.Equal(0, await queue.EnqueuePendingAsync(default));
        var claim = await jobs.ClaimNextAsync("recording-worker", [RecordingMusicBrainzEnrichmentQueue.JobType]);
        Assert.NotNull(claim);
        var handler = new RecordingMusicBrainzEnrichmentJobHandler(_factory, client.Object, settings, _service);
        var result = await handler.ExecuteAsync(new(claim, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await jobs.CompleteAsync(claim, result);
        Assert.Equal(mbid, (await _service.ResolveRecordingAsync(Actor(_userA), recording.Recording.PublicId))?.MusicBrainzRecordingId);
        Assert.Equal(0, await queue.EnqueuePendingAsync(default));
        client.Verify(item => item.LookupByIsrcAsync("USRC17607839", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingEvidenceJob_UpstreamFailureOnlyRetriesOptionalWork(bool rateLimited)
    {
        var recording = await _service.CreateRecordingAsync(Actor(_userA), "source", "USRC17607839");
        var client = new Mock<IMusicBrainzCatalogClient>();
        client.SetupGet(item => item.ConfiguredSourceRevision).Returns("fixture:ws2");
        client.Setup(item => item.LookupByIsrcAsync("USRC17607839", It.IsAny<CancellationToken>()))
            .ThrowsAsync(rateLimited
                ? new MusicBrainzLookupException("catalog_source_temporarily_unavailable", "Source unavailable.", true, TimeSpan.FromSeconds(7))
                : new HttpRequestException("Fixture outage"));
        var settings = Options.Create(new MusicBrainzSettings { Enabled = true });
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        await new RecordingMusicBrainzEnrichmentQueue(_factory, jobs, settings, client.Object, _clock).EnqueuePendingAsync(default);
        var claim = await jobs.ClaimNextAsync("recording-worker", [RecordingMusicBrainzEnrichmentQueue.JobType]);
        var handler = new RecordingMusicBrainzEnrichmentJobHandler(_factory, client.Object, settings, _service);
        var result = await handler.ExecuteAsync(new(claim!, Mock.Of<IServiceProvider>()), default);
        Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
        Assert.Equal(rateLimited ? TimeSpan.FromSeconds(7) : null, result.RetryDelay);
        var current = await _service.ResolveRecordingAsync(Actor(_userA), recording.Recording.PublicId);
        Assert.Equal(recording.Recording.Id, current?.Id);
        Assert.Null(current?.MusicBrainzRecordingId);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(new(claim!, Mock.Of<IServiceProvider>()), canceled.Token));
    }

    private static Mock<IEffectiveProviderPolicyResolver> PolicyResolver(
        Guid userId, params (string ProviderId, ProviderUseMode Mode)[] modes)
    {
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty,
            "lossless",
            0)
        {
            UserId = userId,
            ProviderModes = modes.ToImmutableDictionary(
                item => item.ProviderId, item => item.Mode, StringComparer.OrdinalIgnoreCase)
        };
        var resolver = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.ResolveForUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);
        resolver.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);
        return resolver;
    }
}
