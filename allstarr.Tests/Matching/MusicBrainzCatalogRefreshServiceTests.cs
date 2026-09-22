using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Services.MusicBrainz;
using Moq;

namespace allstarr.Tests;

public sealed class MusicBrainzCatalogRefreshServiceTests
{
    [Fact]
    public async Task RefreshRelease_LoadsBoundedHierarchyAndPassesSourceStampToAtomicIngest()
    {
        const string releaseId = "11111111-1111-4111-8111-111111111111";
        const string groupId = "22222222-2222-4222-8222-222222222222";
        const string artistId = "33333333-3333-4333-8333-333333333333";
        var now = new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.Zero);
        var artist = new MusicBrainzArtist { Id = artistId, Name = "Artist" };
        var credit = new MusicBrainzArtistCredit { Name = "Artist", Artist = artist };
        var release = new MusicBrainzRelease
        {
            Id = releaseId,
            Title = "Release",
            ReleaseGroup = new MusicBrainzReleaseGroup { Id = groupId, Title = "Group" },
            ArtistCredit = [credit],
            Media =
            [
                new MusicBrainzMedium
                {
                    Position = 1,
                    Tracks =
                    [
                        new MusicBrainzReleaseTrack
                        {
                            Id = "44444444-4444-4444-8444-444444444444",
                            Position = 1,
                            Title = "Track",
                            Recording = new MusicBrainzRecording
                            {
                                Id = "55555555-5555-4555-8555-555555555555",
                                Title = "Track",
                                ArtistCredit = [credit]
                            }
                        }
                    ]
                }
            ]
        };
        var group = new MusicBrainzReleaseGroup
        {
            Id = groupId,
            Title = "Group",
            ArtistCredit = [credit]
        };
        var client = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        client.SetupGet(item => item.ConfiguredSourceId).Returns("brainzmash");
        client.SetupGet(item => item.ConfiguredSourceRevision).Returns("brainzmash:ws2");
        client.Setup(item => item.LookupReleaseByMbidAsync(releaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(release);
        client.Setup(item => item.LookupReleaseGroupByMbidAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        client.Setup(item => item.LookupArtistByMbidAsync(artistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artist);
        var expected = new MusicBrainzCatalogIngestResult(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 1, 1, 1, 5,
            new CanonicalCatalogEvidenceResult(5, 0, 5, 0));
        var ingest = new Mock<IMusicBrainzCatalogIngestService>(MockBehavior.Strict);
        ingest.Setup(item => item.IngestAsync(
                It.IsAny<ProviderActorContext>(),
                It.Is<MusicBrainzCatalogGraph>(graph =>
                    graph.Release == release &&
                    graph.ReleaseGroup == group &&
                    graph.Artists.Count == 1 &&
                    graph.Artists.Single() == artist),
                It.Is<MusicBrainzCatalogSource>(source =>
                    source.SourceId == "brainzmash" &&
                    source.SourceRevision == "brainzmash:ws2" &&
                    source.ObservedAt == now &&
                    source.RefreshAfter == now.AddDays(7)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var clock = new Mock<IPlatformClock>(MockBehavior.Strict);
        clock.SetupGet(item => item.UtcNow).Returns(now);
        var service = new MusicBrainzCatalogRefreshService(client.Object, ingest.Object, clock.Object);

        var result = await service.RefreshReleaseAsync(Actor(), releaseId);

        Assert.Same(expected, result);
        client.VerifyAll();
        ingest.VerifyAll();
    }

    [Fact]
    public async Task RefreshRelease_RejectsIncompleteHierarchyBeforeIngest()
    {
        const string releaseId = "11111111-1111-4111-8111-111111111111";
        var client = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        client.Setup(item => item.LookupReleaseByMbidAsync(releaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MusicBrainzRelease { Id = releaseId, Title = "No group" });
        var ingest = new Mock<IMusicBrainzCatalogIngestService>(MockBehavior.Strict);
        var clock = new Mock<IPlatformClock>(MockBehavior.Strict);
        var service = new MusicBrainzCatalogRefreshService(client.Object, ingest.Object, clock.Object);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RefreshReleaseAsync(Actor(), releaseId));

        ingest.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DurableRefreshHandler_PreservesRetryableCatalogFailure()
    {
        const string releaseId = "11111111-1111-4111-8111-111111111111";
        var refresh = new Mock<IMusicBrainzCatalogRefreshService>(MockBehavior.Strict);
        refresh.Setup(item => item.RefreshReleaseAsync(
                It.IsAny<ProviderActorContext>(), releaseId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MusicBrainzLookupException(
                "catalog_source_temporarily_unavailable",
                "Catalog unavailable.",
                true,
                TimeSpan.FromSeconds(9)));
        var handler = new MusicBrainzCatalogRefreshJobHandler(refresh.Object);
        var claim = new DurableJobClaim(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            MusicBrainzCatalogRefreshJobHandler.Type,
            JsonSerializer.SerializeToElement(new MusicBrainzCatalogRefreshJobPayload(releaseId)),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            null,
            null,
            null,
            JsonSerializer.SerializeToElement(new { }),
            "catalog-test",
            "worker",
            DateTimeOffset.UtcNow.AddMinutes(1));

        var result = await handler.ExecuteAsync(
            new DurableJobExecutionContext(claim, Mock.Of<IServiceProvider>()),
            CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
        Assert.Equal("catalog_source_temporarily_unavailable", result.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(9), result.RetryDelay);
        refresh.VerifyAll();
    }

    [Fact]
    public async Task DiscoveryHandler_DeduplicatesEditionsAndQueuesBoundedRefreshes()
    {
        const string recordingId = "11111111-1111-4111-8111-111111111111";
        const string firstReleaseId = "22222222-2222-4222-8222-222222222222";
        const string secondReleaseId = "33333333-3333-4333-8333-333333333333";
        var client = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        client.Setup(item => item.LookupByMbidAsync(recordingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MusicBrainzRecording
            {
                Id = recordingId,
                Title = "Track",
                Releases =
                [
                    new MusicBrainzRelease { Id = firstReleaseId },
                    new MusicBrainzRelease { Id = firstReleaseId.ToUpperInvariant() },
                    new MusicBrainzRelease { Id = secondReleaseId }
                ]
            });
        var queue = new Mock<IMusicBrainzCatalogRefreshQueue>(MockBehavior.Strict);
        queue.Setup(item => item.EnqueueReleaseAsync(
                It.Is<ProviderActorContext>(actor =>
                    actor.Kind == ProviderActorKind.SystemJob &&
                    actor.EffectiveUserId.HasValue),
                firstReleaseId,
                "catalog-test",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableJobEnqueueResult(Guid.CreateVersion7(), true));
        queue.Setup(item => item.EnqueueReleaseAsync(
                It.IsAny<ProviderActorContext>(),
                secondReleaseId,
                "catalog-test",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableJobEnqueueResult(Guid.CreateVersion7(), true));
        var progress = new List<DurableJobProgressUpdate>();
        var context = new DurableJobExecutionContext(
            Claim(
                MusicBrainzCatalogDiscoveryJobHandler.Type,
                new MusicBrainzCatalogDiscoveryJobPayload(recordingId)),
            Mock.Of<IServiceProvider>())
        {
            ReportProgressAsync = (update, _) =>
            {
                progress.Add(update);
                return Task.FromResult(true);
            }
        };

        var result = await new MusicBrainzCatalogDiscoveryJobHandler(
            client.Object, queue.Object).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(2, progress.Count);
        Assert.Equal(2, progress[^1].Completed);
        client.VerifyAll();
        queue.VerifyAll();
    }

    [Fact]
    public async Task DiscoveryHandler_RejectsMalformedReleaseIdentityWithoutQueueing()
    {
        const string recordingId = "11111111-1111-4111-8111-111111111111";
        var client = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        client.Setup(item => item.LookupByMbidAsync(recordingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MusicBrainzRecording
            {
                Id = recordingId,
                Releases = [new MusicBrainzRelease { Id = "not-an-mbid" }]
            });
        var queue = new Mock<IMusicBrainzCatalogRefreshQueue>(MockBehavior.Strict);
        var context = new DurableJobExecutionContext(
            Claim(
                MusicBrainzCatalogDiscoveryJobHandler.Type,
                new MusicBrainzCatalogDiscoveryJobPayload(recordingId)),
            Mock.Of<IServiceProvider>());

        var result = await new MusicBrainzCatalogDiscoveryJobHandler(
            client.Object, queue.Object).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
        Assert.Equal("catalog_recording_hierarchy_invalid", result.ErrorCode);
        queue.VerifyNoOtherCalls();
    }

    private static DurableJobClaim Claim<T>(string type, T payload) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        1,
        type,
        JsonSerializer.SerializeToElement(payload),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        null,
        null,
        null,
        JsonSerializer.SerializeToElement(new { }),
        "catalog-test",
        "worker",
        DateTimeOffset.UtcNow.AddMinutes(1));

    private static ProviderActorContext Actor() => new(
        Guid.CreateVersion7(),
        ProviderActorKind.User,
        Guid.CreateVersion7(),
        new ProviderBackendPrincipal("jellyfin", "fixture", "listener"));
}
