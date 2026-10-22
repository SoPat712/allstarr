using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderDownloadDiagnosticTests
{
    [Theory]
    [InlineData(ProviderErrorKind.TransientFailure, null, 2)]
    [InlineData(ProviderErrorKind.NotFound, null, 2)]
    public async Task AutomaticSample_TriesAnotherTrackAfterMissingOrSourceFailure(ProviderErrorKind failure, string? reason, int attempts)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var streaming = new Mock<IProviderDownloadStreamingCapability>(MockBehavior.Strict);
        streaming.SetupGet(item => item.SupportsRecordingAvailability).Returns(false);
        var tracks = new List<string>();
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns<ProviderExecutionContext, ProviderStreamLeaseRequest>((_, request) =>
            {
                tracks.Add(request.TrackId.Value);
                return Task.FromResult(tracks.Count == 1
                    ? ProviderOutcome<ProviderStreamLease>.Failure(new(failure))
                    : ProviderOutcome<ProviderStreamLease>.Success(Lease()));
            });
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture",
                [Track("first"), Track("second")])));
        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var runner = Runner(streaming.Object, metadata.Object, selector);

        var result = await runner.MeasureAsync(Actor(), "fixture", null, ProviderAudioQuality.Any, "sample-failure");

        Assert.Equal(attempts, tracks.Count);
        Assert.Equal(reason == null, result.Succeeded);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(reason == null ? null : "resolve", result.Stage);
        if (reason != null)
        {
            metadata.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Once);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Contains("\"reasonCode\":\"" + reason + "\"", json, StringComparison.Ordinal);
            Assert.Contains("\"stage\":\"resolve\"", json, StringComparison.Ordinal);
        }
        else Assert.Equal(new[] { "first", "second" }, tracks.OrderBy(item => item, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true, null, 180, false, false)]
    [InlineData(true, 60, 60, false, false)]
    [InlineData(true, null, 180, true, false)]
    [InlineData(true, null, 180, false, true)]
    [InlineData(false, null, 30, false, false)]
    public async Task Measurement_UsesOneTotalDeadline(bool download, int? configuredSeconds, int seconds, bool selection, bool household)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderStreamLease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streaming = download ? new Mock<IProviderDownloadStreamingCapability>().As<IProviderStreamingCapability>() : new Mock<IProviderStreamingCapability>();
        ProviderExecutionContext? observed = null;
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) =>
            {
                observed = context;
                entered.TrySetResult();
                return release.Task;
            });
        var metadata = new Mock<IProviderMetadataCapability>();
        var searchRelease = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns<ProviderExecutionContext, ProviderMetadataSearchRequest>((context, _) =>
            {
                observed = context;
                entered.TrySetResult();
                return searchRelease.Task;
            });
        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var options = new ProtocolExecutionOptions();
        if (configuredSeconds.HasValue) options.DownloadDiagnosticTimeoutSeconds = configuredSeconds.Value;
        var runner = Runner(streaming.Object, metadata.Object, selector, options, clock);
        var pending = runner.MeasureAsync(Actor(), "fixture", null, ProviderAudioQuality.Any, "bounded-measurement",
            selection ? null : "track", householdCheck: household);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(clock.GetUtcNow().AddSeconds(seconds), observed!.Deadline);
        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        try
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.Succeeded);
            Assert.Equal(selection ? "sample-unavailable" : "timeout", result.ReasonCode);
            Assert.Equal(selection ? "track-selection" : "resolve", result.Stage);
            Assert.Equal(selection ? 409 : 504, result.StatusCode);
            Assert.Equal(seconds, result.Limit.TimeoutSeconds);
            Assert.True(observed.CancellationToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult(ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled)));
            searchRelease.TrySetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", [])));
        }
    }

    [Fact]
    public async Task Measurement_DoesNotRestartItsBudgetAfterTrackSelection()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderStreamLease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(() =>
            {
                clock.Advance(TimeSpan.FromSeconds(100));
                return Task.FromResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", [Track("first")])));
            });
        var streaming = new Mock<IProviderDownloadStreamingCapability>();
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) =>
            {
                entered.TrySetResult();
                return release.Task.WaitAsync(context.CancellationToken);
            });
        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var runner = Runner(streaming.Object, metadata.Object, selector, clock: clock);
        var pending = runner.MeasureAsync(Actor(), "fixture", null, ProviderAudioQuality.Any, "shared-budget");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(79));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("sample-unavailable", result.ReasonCode);
        Assert.Equal("resolve", result.Stage);
    }

    [Fact]
    public async Task Measurement_DoesNotWaitPastItsCapToRecordTheResult()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AllstarrDbContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>();
        factory.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        var streaming = new Mock<IProviderDownloadStreamingCapability>();
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(Lease()));
        using var selector = new ProviderCtsTrackSelector(Mock.Of<IDbContextFactory<AllstarrDbContext>>());
        var runner = Runner(streaming.Object, Mock.Of<IProviderMetadataCapability>(), selector, clock: clock, audit: factory.Object);
        var pending = runner.MeasureAsync(Actor(), "fixture", null, ProviderAudioQuality.Any, "record-budget", "track");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(3));
        try
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("timeout", result.ReasonCode);
            Assert.Equal("recording", result.Stage);
        }
        finally { release.TrySetCanceled(); }
    }

    private static ProviderCtsDiagnosticRunner Runner(IProviderStreamingCapability streaming, IProviderMetadataCapability metadata,
        ProviderCtsTrackSelector selector, ProtocolExecutionOptions? options = null, TimeProvider? clock = null,
        IDbContextFactory<AllstarrDbContext>? audit = null)
    {
        var descriptor = new ProviderDescriptor("fixture", "Fixture", "Fixture", ProviderOrigin.Extension, "1", "1",
            [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["getStreamLease"], [])], new());
        var registry = new Mock<IProviderRegistry>();
        IProviderStreamingCapability? registered = streaming;
        IProviderMetadataCapability? registeredMetadata = metadata;
        registry.Setup(item => item.TryGetCapability<IProviderStreamingCapability>("fixture", ProviderCapabilityKind.Streaming, out registered)).Returns(true);
        registry.Setup(item => item.TryGetCapability<IProviderMetadataCapability>("fixture", ProviderCapabilityKind.Metadata, out registeredMetadata)).Returns(true);
        registry.Setup(item => item.GetRequired("fixture")).Returns(descriptor);
        return new(registry.Object, Mock.Of<IProviderRouteAccountResolver>(), selector, Mock.Of<IProviderOutcomeObserver>(), audit,
            executionOptions: options, timeProvider: clock);
    }

    private static ProviderActorContext Actor() => new(ProviderActorKind.Administrator, Guid.CreateVersion7(), new("jellyfin", "fixture", "listener"));
    private static ProviderTrackMetadata Track(string id) => new(new("fixture", ProviderResourceKind.Track, id), "Song", [new("Artist")]);
    private static ProviderStreamLease Lease() => new("lease", new("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
        false, false, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry,
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) }));

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
