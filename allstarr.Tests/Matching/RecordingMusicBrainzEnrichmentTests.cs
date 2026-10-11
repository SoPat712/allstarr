using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
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
}
