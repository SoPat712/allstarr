using System.Collections.Concurrent;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Storage;
using Moq;

namespace allstarr.Tests;

public sealed class ObservedProviderCapabilitiesTests
{
    [Fact]
    public async Task Metadata_ForwardsExactArgumentsAndReturnsOriginalOutcome()
    {
        var context = Context();
        var request = new ProviderMetadataSearchRequest("fixture", new(17, "cursor"), "us");
        var outcome = ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", []));
        var capability = Capability<IProviderMetadataCapability>(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.SearchTracksAsync(context, request))
            .Callback<ProviderExecutionContext, ProviderMetadataSearchRequest>((actualContext, actualRequest) =>
            {
                Assert.Same(context, actualContext);
                Assert.Same(request, actualRequest);
            }).ReturnsAsync(outcome);
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap(capability.Object, observer);

        Assert.Equal("fixture", wrapped.ProviderId);
        Assert.Equal(ProviderCapabilityKind.Metadata, wrapped.Capability);
        Assert.Same(outcome, await wrapped.SearchTracksAsync(context, request));
        AssertObservation(observer, context, ProviderCapabilityKind.Metadata, null);
        capability.VerifyAll();
    }

    [Fact]
    public async Task Playlist_PreservesMutationSupportAndOriginalFailure()
    {
        var context = Context();
        var request = new ProviderPlaylistSearchRequest("fixture", new(5));
        var error = new ProviderError(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(37));
        var outcome = ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(error);
        var capability = Capability<IProviderPlaylistCapability>(ProviderCapabilityKind.Playlist);
        var support = new ProviderPlaylistMutationSupport(true, true);
        capability.SetupGet(item => item.MutationSupport).Returns(support);
        capability.Setup(item => item.SearchPlaylistsAsync(context, request))
            .Callback<ProviderExecutionContext, ProviderPlaylistSearchRequest>((actualContext, actualRequest) =>
            {
                Assert.Same(context, actualContext);
                Assert.Same(request, actualRequest);
            }).ReturnsAsync(outcome);
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap(capability.Object, observer);

        Assert.Same(support, wrapped.MutationSupport);
        Assert.Equal(ProviderCapabilityKind.Playlist, wrapped.Capability);
        Assert.Same(outcome, await wrapped.SearchPlaylistsAsync(context, request));
        AssertObservation(observer, context, ProviderCapabilityKind.Playlist, error);
    }

    [Fact]
    public async Task ParallelCalls_KeepAccountsRevisionsAndOutcomesSeparate()
    {
        var first = Context(Guid.NewGuid(), 4);
        var second = Context(Guid.NewGuid(), 9);
        var request = new ProviderTrackLookupRequest(TrackId());
        var firstResult = ProviderOutcome<ProviderTrackMetadata>.Failure(new(ProviderErrorKind.NotFound));
        var secondResult = ProviderOutcome<ProviderTrackMetadata>.Failure(new(ProviderErrorKind.Unauthorized));
        var firstRelease = new TaskCompletionSource<ProviderOutcome<ProviderTrackMetadata>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource<ProviderOutcome<ProviderTrackMetadata>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capability = Capability<IProviderMetadataCapability>(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.GetTrackAsync(first, request)).Returns(firstRelease.Task);
        capability.Setup(item => item.GetTrackAsync(second, request)).Returns(secondRelease.Task);
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap(capability.Object, observer);

        var firstCall = wrapped.GetTrackAsync(first, request);
        var secondCall = wrapped.GetTrackAsync(second, request);
        Assert.Empty(observer.Observations);
        secondRelease.SetResult(secondResult);
        Assert.Same(secondResult, await secondCall);
        firstRelease.SetResult(firstResult);
        Assert.Same(firstResult, await firstCall);

        var observations = observer.Observations.ToArray();
        Assert.Equal(2, observations.Length);
        Assert.Same(second, observations[0].Context);
        Assert.Equal(9, observations[0].Context.Account!.Revision);
        Assert.Same(secondResult.Error, observations[0].Error);
        Assert.Same(first, observations[1].Context);
        Assert.Equal(4, observations[1].Context.Account!.Revision);
        Assert.Same(firstResult.Error, observations[1].Error);
        Assert.NotEqual(observations[0].Context.Account!.AccountId, observations[1].Context.Account!.AccountId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingLease_ObservesOnlyFailure(bool success)
    {
        var context = Context();
        var request = new ProviderStreamLeaseRequest(TrackId(), ProviderAudioQuality.Lossless, 42);
        var outcome = success
            ? ProviderOutcome<ProviderStreamLease>.Success(new("fixture-lease", new Uri("https://media.example.test/fixture"),
                DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry))
            : ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.TransientFailure));
        var capability = Capability<IProviderStreamingCapability>(ProviderCapabilityKind.Streaming);
        capability.Setup(item => item.GetStreamLeaseAsync(context, request)).ReturnsAsync(outcome);
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap(capability.Object, observer);

        Assert.Equal(ProviderCapabilityKind.Streaming, wrapped.Capability);
        Assert.Same(outcome, await wrapped.GetStreamLeaseAsync(context, request));
        if (success) Assert.Empty(observer.Observations);
        else AssertObservation(observer, context, ProviderCapabilityKind.Streaming, outcome.Error);
    }

    [Theory]
    [InlineData("unauthorized", ProviderErrorKind.Unauthorized)]
    [InlineData("timeout", ProviderErrorKind.TransientFailure)]
    [InlineData("cancellation", ProviderErrorKind.TransientFailure)]
    [InlineData("other", ProviderErrorKind.TransientFailure)]
    public async Task Exceptions_AreClassifiedWithoutReplacingOrExposingTheException(string kind, ProviderErrorKind expected)
    {
        var context = Context();
        var request = new ProviderTrackLookupRequest(TrackId());
        Exception exception = kind switch
        {
            "unauthorized" => new UnauthorizedAccessException("fixture-secret"),
            "timeout" => new TimeoutException("fixture-secret"),
            "cancellation" => new OperationCanceledException("fixture-secret"),
            _ => new InvalidOperationException("fixture-secret")
        };
        var capability = Capability<IProviderMetadataCapability>(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.GetTrackAsync(context, request)).Throws(exception);
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap(capability.Object, observer);

        Assert.Same(exception, await Record.ExceptionAsync(() => wrapped.GetTrackAsync(context, request)));
        var observation = Assert.Single(observer.Observations);
        Assert.Same(context, observation.Context);
        Assert.Equal(expected, observation.Error!.Kind);
        Assert.DoesNotContain("fixture-secret", observation.Error.ToString());
    }

    [Fact]
    public async Task CallerCancellation_IsRethrownWithoutObservation()
    {
        using var cancellation = new CancellationTokenSource();
        var context = Context(cancellationToken: cancellation.Token);
        var request = new ProviderTrackLookupRequest(TrackId());
        var exception = new OperationCanceledException(cancellation.Token);
        var capability = Capability<IProviderMetadataCapability>(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.GetTrackAsync(context, request))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromException<ProviderOutcome<ProviderTrackMetadata>>(exception);
            });
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap(capability.Object, observer);

        Assert.Same(exception, await Record.ExceptionAsync(() => wrapped.GetTrackAsync(context, request)));
        Assert.Empty(observer.Observations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObserverException_DoesNotChangeReturnedOutcome(bool success)
    {
        var context = Context();
        var request = new ProviderMetadataSearchRequest("fixture", new());
        var outcome = success
            ? ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", []))
            : ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(new(ProviderErrorKind.Forbidden));
        var capability = Capability<IProviderMetadataCapability>(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.SearchTracksAsync(context, request)).ReturnsAsync(outcome);
        var observer = new RecordingObserver { Throw = true };

        Assert.Same(outcome, await ObservedProviderCapabilities.Wrap(capability.Object, observer).SearchTracksAsync(context, request));
        AssertObservation(observer, context, ProviderCapabilityKind.Metadata, outcome.Error);
    }

    [Fact]
    public async Task ObserverException_DoesNotReplaceProviderException()
    {
        var context = Context();
        var request = new ProviderTrackLookupRequest(TrackId());
        var exception = new UnauthorizedAccessException("fixture-secret");
        var capability = Capability<IProviderMetadataCapability>(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.GetTrackAsync(context, request)).ThrowsAsync(exception);
        var observer = new RecordingObserver { Throw = true };

        Assert.Same(exception, await Record.ExceptionAsync(() =>
            ObservedProviderCapabilities.Wrap(capability.Object, observer).GetTrackAsync(context, request)));
        Assert.Equal(ProviderErrorKind.Unauthorized, Assert.Single(observer.Observations).Error!.Kind);
    }

    [Fact]
    public async Task Download_ForwardsProgressObjectWithoutReplacingFailure()
    {
        var context = Context();
        var request = new ProviderDownloadRequest(TrackId(), Guid.NewGuid(), new("workspace"), ProviderAudioQuality.Lossless);
        var progress = Mock.Of<IProgress<ProviderDownloadProgress>>();
        var outcome = ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.NotFound));
        var capability = Capability<IProviderDownloadCapability>(ProviderCapabilityKind.Download);
        capability.Setup(item => item.DownloadAsync(context, request, progress))
            .Callback<ProviderExecutionContext, ProviderDownloadRequest, IProgress<ProviderDownloadProgress>?>((actualContext, actualRequest, actualProgress) =>
            {
                Assert.Same(context, actualContext);
                Assert.Same(request, actualRequest);
                Assert.Same(progress, actualProgress);
            }).ReturnsAsync(outcome);
        var observer = new RecordingObserver();

        Assert.Same(outcome, await ObservedProviderCapabilities.Wrap(capability.Object, observer).DownloadAsync(context, request, progress));
        AssertObservation(observer, context, ProviderCapabilityKind.Download, outcome.Error);
    }

    [Fact]
    public async Task Lyrics_ForwardsRequestAndOriginalOutcome()
    {
        var context = Context();
        var request = new ProviderLyricsRequest(Guid.NewGuid(), TrackId());
        var outcome = ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.NotFound));
        var capability = Capability<IProviderLyricsCapability>(ProviderCapabilityKind.Lyrics);
        capability.Setup(item => item.FetchLyricsAsync(context, request)).ReturnsAsync(outcome);
        var observer = new RecordingObserver();

        Assert.Same(outcome, await ObservedProviderCapabilities.Wrap(capability.Object, observer).FetchLyricsAsync(context, request));
        AssertObservation(observer, context, ProviderCapabilityKind.Lyrics, outcome.Error);
    }

    [Fact]
    public async Task Intelligence_ForwardsSeedCollectionsAndLimit()
    {
        var context = Context();
        IReadOnlyList<string> positive = ["one"];
        IReadOnlyList<string> negative = ["two"];
        var outcome = ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success([]);
        var capability = Capability<IProviderIntelligenceCapability>(ProviderCapabilityKind.Intelligence);
        capability.Setup(item => item.BlendAsync(context, positive, negative, 13))
            .Callback<ProviderExecutionContext, IReadOnlyList<string>, IReadOnlyList<string>, int>((actualContext, actualPositive, actualNegative, limit) =>
            {
                Assert.Same(context, actualContext);
                Assert.Same(positive, actualPositive);
                Assert.Same(negative, actualNegative);
                Assert.Equal(13, limit);
            }).ReturnsAsync(outcome);
        var observer = new RecordingObserver();

        Assert.Same(outcome, await ObservedProviderCapabilities.Wrap(capability.Object, observer).BlendAsync(context, positive, negative, 13));
        AssertObservation(observer, context, ProviderCapabilityKind.Intelligence, null);
    }

    [Theory]
    [InlineData(ProviderProbeStatus.Healthy, null)]
    [InlineData(ProviderProbeStatus.Degraded, ProviderErrorKind.TransientFailure)]
    [InlineData(ProviderProbeStatus.Unavailable, ProviderErrorKind.TransientFailure)]
    [InlineData(ProviderProbeStatus.Unauthorized, ProviderErrorKind.Unauthorized)]
    public async Task HealthProbe_ObservesTargetCapabilityAndPayloadStatus(ProviderProbeStatus status, ProviderErrorKind? expected)
    {
        var context = Context();
        var request = new ProviderHealthProbeRequest(ProviderCapabilityKind.Download);
        var outcome = ProviderOutcome<ProviderHealthProbeResult>.Success(new(status, DateTimeOffset.UtcNow, TimeSpan.Zero));
        var capability = Capability<IProviderHealthProbeCapability>(ProviderCapabilityKind.Health);
        capability.Setup(item => item.ProbeAsync(context, request)).ReturnsAsync(outcome);
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap(capability.Object, observer);

        Assert.Equal(ProviderCapabilityKind.Health, wrapped.Capability);
        Assert.Same(outcome, await wrapped.ProbeAsync(context, request));
        var observation = Assert.Single(observer.Observations);
        Assert.Same(context, observation.Context);
        Assert.Equal(ProviderCapabilityKind.Download, observation.Capability);
        Assert.Equal(expected, observation.Error?.Kind);
    }

    [Fact]
    public async Task HealthProbe_PreservesFailureAndTargetsItsRequestedCapability()
    {
        var context = Context();
        var request = new ProviderHealthProbeRequest(ProviderCapabilityKind.Streaming);
        var outcome = ProviderOutcome<ProviderHealthProbeResult>.Failure(new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(11)));
        var capability = Capability<IProviderHealthProbeCapability>(ProviderCapabilityKind.Health);
        capability.Setup(item => item.ProbeAsync(context, request)).ReturnsAsync(outcome);
        var observer = new RecordingObserver();

        Assert.Same(outcome, await ObservedProviderCapabilities.Wrap(capability.Object, observer).ProbeAsync(context, request));
        AssertObservation(observer, context, ProviderCapabilityKind.Streaming, outcome.Error);
    }

    [Fact]
    public async Task DefaultMetadataMethods_KeepTheirOriginalFailures()
    {
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap<IProviderMetadataCapability>(new DefaultMetadata(), observer);
        var context = Context();
        var request = new ProviderArtistItemsRequest(new("fixture", ProviderResourceKind.Artist, "artist"), new());

        Assert.Equal(ProviderErrorKind.NotSupported,
            (await wrapped.GetPlaylistArtworkAsync(context, new("fixture", ProviderResourceKind.Playlist, "playlist"))).Error!.Kind);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, (await wrapped.GetArtistAlbumsAsync(context, request)).Error!.Kind);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, (await wrapped.GetArtistTracksAsync(context, request)).Error!.Kind);
        Assert.Equal(3, observer.Observations.Count);
    }

    [Fact]
    public async Task DefaultPlaylistMethods_KeepTheirOriginalFailuresAndMutationSupport()
    {
        var observer = new RecordingObserver();
        var wrapped = ObservedProviderCapabilities.Wrap<IProviderPlaylistCapability>(new DefaultPlaylist(), observer);
        var context = Context();

        Assert.Same(ProviderPlaylistMutationSupport.None, wrapped.MutationSupport);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable,
            (await wrapped.ResolveArtworkAsync(context, new(new(publicUri: new Uri("https://artwork.example.test/fixture"))))).Error!.Kind);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable,
            (await wrapped.MutatePlaylistAsync(context, new("fixture", "fixture", [], ProviderPlaylistConflictBehavior.Recreate))).Error!.Kind);
        Assert.Equal(2, observer.Observations.Count);
    }

    [Fact]
    public void UnrecognizedCapability_IsReturnedUnchanged()
    {
        IProviderCapability implementation = new UnknownCapability();
        Assert.Same(implementation, ObservedProviderCapabilities.Wrap(implementation, new RecordingObserver()));
    }

    private static Mock<TCapability> Capability<TCapability>(ProviderCapabilityKind kind)
        where TCapability : class, IProviderCapability
    {
        var capability = new Mock<TCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("fixture");
        capability.SetupGet(item => item.Capability).Returns(kind);
        return capability;
    }

    private static ProviderExternalResourceId TrackId() => new("fixture", ProviderResourceKind.Track, "track");

    private static ProviderExecutionContext Context(Guid? accountId = null, long revision = 1,
        CancellationToken cancellationToken = default)
    {
        var userId = Guid.NewGuid();
        return new(new(ProviderActorKind.User, userId, new("jellyfin", "fixture", "fixture-user")), "fixture",
            new(accountId ?? Guid.NewGuid(), "fixture", ProviderAccountScope.Personal, revision, ownerUserId: userId),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, false, true, ["fixture"]),
            "fixture-operation", "fixture-correlation", DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
    }

    private static void AssertObservation(RecordingObserver observer, ProviderExecutionContext context,
        ProviderCapabilityKind capability, ProviderError? error)
    {
        var observation = Assert.Single(observer.Observations);
        Assert.Same(context, observation.Context);
        Assert.Equal(capability, observation.Capability);
        Assert.Same(error, observation.Error);
        Assert.True(observation.Elapsed >= TimeSpan.Zero);
    }

    private sealed record Observation(ProviderExecutionContext Context, ProviderCapabilityKind Capability,
        ProviderError? Error, TimeSpan Elapsed);

    private sealed class RecordingObserver : IProviderOutcomeObserver
    {
        public ConcurrentQueue<Observation> Observations { get; } = new();
        public bool Throw { get; init; }
        public void Observe(ProviderExecutionContext context, ProviderCapabilityKind capability, ProviderError? error, TimeSpan elapsed)
        {
            Observations.Enqueue(new(context, capability, error, elapsed));
            if (Throw) throw new InvalidOperationException("fixture observer failure");
        }
    }

    private sealed class UnknownCapability : IProviderCapability
    {
        public string ProviderId => "fixture";
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;
    }

    private sealed class DefaultMetadata : IProviderMetadataCapability
    {
        public string ProviderId => "fixture";
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;
        public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(ProviderExecutionContext context, ProviderTrackLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(ProviderExecutionContext context, ProviderIsrcLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(ProviderExecutionContext context, ProviderAlbumLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(ProviderExecutionContext context, ProviderArtistLookupRequest request) => throw new NotSupportedException();
    }

    private sealed class DefaultPlaylist : IProviderPlaylistCapability
    {
        public string ProviderId => "fixture";
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;
        public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(ProviderExecutionContext context, ProviderUserPlaylistsRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(ProviderExecutionContext context, ProviderPlaylistTracksRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(ProviderExecutionContext context, ProviderPlaylistSearchRequest request) => throw new NotSupportedException();
    }
}
