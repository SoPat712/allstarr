using allstarr.Core.Matching;
using System.Collections.Immutable;
using allstarr.Core.Settings;
using allstarr.Services.Lyrics;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;
using System.Net;
using System.Collections.Concurrent;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolLyricsAutomaticTests
{
    [Fact]
    public async Task SpicyAutomaticMissingAliasDoesNotStartExternalDiscovery()
    {
        var recording = Guid.NewGuid();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), recording.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Song>());
        var links = new Mock<ICrossProviderLinkResolver>(MockBehavior.Strict);
        var source = new Source("spicylyrics", Result("spicylyrics", false));
        await using var fixture = new Fixture(identities.Object, links.Object, source)
        { Recording = recording, ExternalProvider = "deezer", ExternalId = "42" };
        Assert.Null(await fixture.FindAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, source.Calls);
        identities.VerifyAll();
        links.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SpicyDeferredAliasPreparationCannotHoldAnEarlierAsynchronousAnswer()
    {
        var aliasEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aliasRelease = new TaskCompletionSource<IReadOnlyList<Song>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = Guid.NewGuid();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), recording.ToString("D"), It.IsAny<CancellationToken>()))
            .Returns(() => { aliasEntered.TrySetResult(); return aliasRelease.Task; });
        var links = new Mock<ICrossProviderLinkResolver>(MockBehavior.Strict);
        var first = new Source("lrclib");
        var later = new Source("spicylyrics", Result("spicylyrics", false));
        await using var fixture = new Fixture(identities.Object, links.Object, first, later)
        { Recording = recording, ExternalProvider = "deezer", ExternalId = "42" };
        var pending = fixture.FindAsync();
        try
        {
            await StartAsync(pending, first);
            first.Completion.TrySetResult(Result("lrclib", false));
            Assert.Equal("lrclib", (await pending.WaitAsync(TimeSpan.FromSeconds(10)))?.Source);
            Assert.False(aliasEntered.Task.IsCompleted);
            Assert.Equal(0, later.Calls);
            links.VerifyNoOtherCalls();
            identities.VerifyNoOtherCalls();
        }
        finally { aliasRelease.TrySetResult([]); first.CompleteAll(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task SpicyOwnedLateCompletionWarmsTheSameForegroundBinding()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("syllable"))
        { BeforeResponse = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        using var http = new HttpClient(handler);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new SpicyLyricsTestCache();
        var source = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), cache, clock: clock);
        var higher = new Source("lrclib");
        var lower = new Source("lrcred", Result("lrcred", false));
        await using var fixture = new Fixture(clock, higher, source, lower) { UseAccount = true, SpotifyId = SpicyLyricsTestData.TrackId };
        var snapshot = await ((IInternalLyricsCapability)source).CaptureSourceAsync(default);
        ((ILyricsOutcomeObserver)fixture.Health).ObserveLyrics(SpicyLyricsTestData.Context(account: fixture.Account), null, TimeSpan.Zero, snapshot.ConfigurationId);
        var pending = fixture.FindAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
            fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
            Assert.Equal("lrcred", (await pending.WaitAsync(TimeSpan.FromSeconds(10)))?.Source);
            release.TrySetResult();
            await cache.Written.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var foreground = new ProviderExecutionContext(fixture.Protocol.Actor!, "spicylyrics",
                new(fixture.Account, "spicylyrics", ProviderAccountScope.Shared, 1, secretReferenceId: SpicyLyricsTestData.SecretId),
                SpicyLyricsTestData.Context().Policy, "foreground", "different-correlation", clock.GetUtcNow().AddMinutes(1), default);
            var replay = await new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), cache, clock: clock)
                .FetchLyricsAsync(foreground, SpicyLyricsTestData.Request());
            Assert.Equal("spicy_lyrics", replay.RequireValue().Attribution!.Catalog);
            Assert.Equal(1, handler.Calls);
        }
        finally { release.TrySetResult(); higher.CompleteAll(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task ImmediateEarlierAnswerDoesNotStartLaterHeldIdentityDiscovery()
    {
        var release = new TaskCompletionSource<IReadOnlyList<CrossProviderLinkCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var links = new Mock<ICrossProviderLinkResolver>(MockBehavior.Strict);
        links.Setup(item => item.ResolveAsync(It.IsAny<ExternalTrackMatchSnapshot>(), It.IsAny<IReadOnlyList<ProviderExternalResourceId>>(),
                It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => { called.TrySetResult(); return release.Task; });
        var first = new Source("lrclib", Result("lrclib", false));
        var later = new Source("spotify", Result("spotify", true));
        await using var fixture = new Fixture(links.Object, first, later) { ExternalProvider = "deezer", ExternalId = "source" };
        try
        {
            var pending = fixture.FindAsync();
            await Task.WhenAny(pending, called.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(called.Task.IsCompleted);
            Assert.Equal("lrclib", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
            Assert.Equal(0, later.Calls);
            links.VerifyNoOtherCalls();
        }
        finally { release.TrySetResult([]); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsynchronousEarlierAnswerAvoidsDiscoveryWhileOtherEligibleWorkRuns(bool knownRecording)
    {
        var links = new Mock<ICrossProviderLinkResolver>(MockBehavior.Strict);
        var first = new Source("lrclib");
        var other = new Source("lrcred");
        var deferred = new Source("spotify", Result("spotify", true));
        await using var fixture = new Fixture(links.Object, first, other, deferred) { ExternalProvider = "deezer", ExternalId = "source", Recording = knownRecording ? Guid.NewGuid() : null };
        var pending = fixture.FindAsync();
        await StartAsync(pending, first);
        await StartAsync(pending, other);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        first.Completion.SetResult(Result("lrclib", false));
        Assert.Equal("lrclib", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.False(other.Completion.Task.IsCompleted);
        Assert.Equal(knownRecording ? 1 : 0, deferred.Calls);
        links.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DeferredDiscoveryRunsAfterMissesAndUsesOnlyVerifiedSelectors(bool verified, bool knownRecording)
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<CrossProviderLinkCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var links = new Mock<ICrossProviderLinkResolver>(MockBehavior.Strict);
        links.Setup(item => item.ResolveAsync(It.IsAny<ExternalTrackMatchSnapshot>(), It.IsAny<IReadOnlyList<ProviderExternalResourceId>>(),
                It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => { called.TrySetResult(); return release.Task; });
        var first = new Source("lrclib", ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, "lrclib")));
        var later = new Source("spotify", Result("spotify", true));
        await using var fixture = new Fixture(links.Object, first, later) { ExternalProvider = "deezer", ExternalId = "source", Recording = knownRecording ? Guid.NewGuid() : null, RejectRecordingTarget = knownRecording };
        try
        {
            var pending = fixture.FindAsync();
            await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, first.Calls);
            Assert.True(first.ActualCalls.All(call => call.Task.IsCompleted) || first.Immediate != null);
            Assert.Equal(0, later.Calls);
            release.TrySetResult([new("spotify", "verified-track", verified, verified ? 1 : 0.5, "fixture")]);
            var answer = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(verified ? "spotify" : null, answer?.Source);
            Assert.Equal(verified ? 1 : 0, later.Calls);
            if (verified) Assert.Equal("verified-track", Assert.Single(later.Requests).ProviderTrackId.Value);
            links.Verify(item => item.ResolveAsync(It.IsAny<ExternalTrackMatchSnapshot>(), It.IsAny<IReadOnlyList<ProviderExternalResourceId>>(),
                It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { release.TrySetResult([]); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDeadlineAfterDiscoveryStartsPreventsAnyLaterSourceCall(bool deadline)
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<CrossProviderLinkCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var links = new Mock<ICrossProviderLinkResolver>(MockBehavior.Strict);
        links.Setup(item => item.ResolveAsync(It.IsAny<ExternalTrackMatchSnapshot>(), It.IsAny<IReadOnlyList<ProviderExternalResourceId>>(),
                It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => { called.TrySetResult(); return release.Task; });
        var first = new Source("lrclib", ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, "lrclib")));
        var later = new Source("spotify", Result("spotify", true));
        await using var fixture = new Fixture(links.Object, first, later) { ExternalProvider = "deezer", ExternalId = "source" };
        using var cancellation = new CancellationTokenSource();
        try
        {
            var pending = fixture.FindAsync(token: cancellation.Token, deadline: deadline ? TimeSpan.FromSeconds(2) : null);
            await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (deadline)
            {
                await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            }
            else cancellation.Cancel();
            release.TrySetResult([new("spotify", "verified-track", true, 1, "fixture")]);
            if (deadline) Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, later.Calls);
        }
        finally { cancellation.Cancel(); release.TrySetResult([]); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalSourcePreparationDoesNotHideAuthorizationOrCancellation(bool canceled)
    {
        var source = new Source("lrclib") { CaptureError = canceled ? new OperationCanceledException(new CancellationToken(true)) : new UnauthorizedAccessException() };
        await using var fixture = new Fixture(source);
        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.FindAsync());
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.FindAsync());
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task HeldActualSourceAnswerBecomingInvalidIsDiscardedForAnotherValidAnswer()
    {
        var endpoint = "https://lyrics.example.test/";
        var handler = new WaitingHandler();
        using var http = new HttpClient(handler);
        var optional = new LyricsPlusCapabilityAdapter(http, null, _ => Task.FromResult(endpoint));
        var higher = new Source("lrclib");
        var other = new Source("lrcred", Result("lrcred", true));
        await using var fixture = new Fixture(higher, optional, other);
        var snapshot = await ((IInternalLyricsCapability)optional).CaptureSourceAsync(default);
        ((ILyricsOutcomeObserver)fixture.Health).ObserveLyrics(Context("lyricsplus", default), null, TimeSpan.Zero, snapshot.ConfigurationId);
        var pending = fixture.FindAsync();
        await StartAsync(pending, higher);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StartAsync(pending, other);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        handler.Response.TrySetResult(new(HttpStatusCode.OK)
        {
            Content = new StringContent(
            "{\"type\":\"Line\",\"lyrics\":[{\"time\":1000,\"text\":\"Words\"}]}")
        });
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(1.5));
        endpoint = "file:///unavailable";
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal("lrcred", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AliasedOrderConsumesOneCanonicalAttemptAndRetainsItsSlot()
    {
        var apple = new Source("apple-musickit");
        var lower = new Source("lrclib", Result("lrclib", true));
        await using var fixture = new Fixture(apple, lower) { ExternalProvider = "apple-musickit", ExternalId = "catalog-track" };
        fixture.CurrentPolicy = fixture.CurrentPolicy with
        {
            ProviderOrders = ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics,
                ["apple-download", "applemusic", "apple-musickit", "lrclib"])
        };
        var pending = fixture.FindAsync();
        await StartAsync(pending, apple);
        await StartAsync(pending, lower);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal("lrclib", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.Equal(1, apple.Calls);
        Assert.False(apple.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task InvalidOptionalSourcePreparationFallsThroughToHealthyRegisteredSource()
    {
        var transport = new WaitingHandler();
        using var http = new HttpClient(transport);
        var invalid = new LyricsPlusCapabilityAdapter(http, null, _ => Task.FromResult("file:///unavailable"));
        var healthy = new Source("lrclib", Result("lrclib", true));
        await using var fixture = new Fixture(invalid, healthy);
        Assert.Equal("lrclib", (await fixture.FindAsync())?.Source);
        Assert.Equal(1, healthy.Calls);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task ObservedInvalidConfigurationPreservesTheTypedSourceFailure()
    {
        var transport = new WaitingHandler();
        using var http = new HttpClient(transport);
        var source = new LyricsPlusCapabilityAdapter(http, null, _ => Task.FromResult("file:///unavailable"));
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(source.ProviderId, "LyricsPlus", "Fixture",
            ProviderOrigin.BuiltIn, "1", "1", [new(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["fetchLyrics"])], new()), [source])]);
        var observed = ObservedProviderCapabilities.Wrap(registry.GetRequiredCapability<IProviderLyricsCapability>(source.ProviderId,
            ProviderCapabilityKind.Lyrics), new ProviderRuntimeHealth());
        var outcome = await observed.FetchLyricsAsync(Context(source.ProviderId, default), new(Guid.NewGuid(),
            new(source.ProviderId, ProviderResourceKind.Track, "track"), trackTitle: "Fixture", artistNames: ["Artist"]));
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, outcome.Error?.Kind);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task LowerAnswerWaitsForPriorityWindowThenWinsWithoutSlowHigherSource()
    {
        var higher = new Source("lrclib");
        var lower = new Source("lrcred", Result("lrcred", false));
        await using var fixture = new Fixture(higher, lower);
        var pending = fixture.FindAsync();
        await StartAsync(pending, higher);
        await StartAsync(pending, lower);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1499));
        Assert.False(pending.IsCompleted);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lrcred", result?.Source);
        Assert.Equal("Words", result?.PlainLyrics);
        Assert.False(higher.Completion.Task.IsCompleted);
        higher.Completion.SetResult(null);
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task EarlierPlainBeatsLaterTimedAndDoesNotStartItWhenAlreadyAvailable()
    {
        var first = new Source("lrclib", Result("lrclib", false));
        var second = new Source("lrcred", Result("lrcred", true));
        await using var fixture = new Fixture(first, second);
        Assert.Equal("lrclib", (await fixture.FindAsync())?.Source);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task ReservedFallbackStartsAtThreeSecondsWhileFourAttemptsRemainOwned()
    {
        var sources = new[] { new Source("lrclib"), new Source("lrcred"), new Source("unison"), new Source("genius"), new Source("paxsenix", Result("paxsenix", true)) };
        await using var fixture = new Fixture(sources);
        var pending = fixture.FindAsync();
        foreach (var source in sources.Take(4)) await StartAsync(pending, source);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        Assert.Equal(0, sources[4].Calls);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await sources[4].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("paxsenix", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        foreach (var source in sources.Take(4)) { Assert.False(source.Completion.Task.IsCompleted); source.Completion.SetResult(null); }
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task PostResponseWorkRetainsAllFourLookupLeasesWithoutAQueue()
    {
        var higher = new Source("lrclib");
        var lower = new Source("lrcred", Result("lrcred", true));
        await using var fixture = new Fixture(higher, lower);
        var pending = Enumerable.Range(0, 4).Select(_ => fixture.FindAsync()).ToArray();
        await lower.WaitForCallsAsync(4);
        for (var index = 0; index < 4; index++)
            await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        foreach (var lookup in pending) Assert.Equal("lrcred", (await lookup.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.Null(await fixture.FindAsync());
        Assert.Equal(4, higher.Calls);
        Assert.Equal(4, lower.Calls);
        higher.Completion.SetResult(null);
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task TwentyActualCallsRemainReservedAfterAllFourCallersAbort()
    {
        var sources = new[] { new Source("lrclib"), new Source("lrcred"), new Source("unison"), new Source("genius"), new Source("paxsenix") };
        foreach (var source in sources) source.PerCall = true;
        await using var fixture = new Fixture(sources);
        var cancellations = Enumerable.Range(0, 4).Select(_ => new CancellationTokenSource()).ToArray();
        try
        {
            var pending = cancellations.Select(item => fixture.FindAsync(token: item.Token)).ToArray();
            foreach (var source in sources.Take(4)) await source.WaitForCallsAsync(4);
            await fixture.Clock.WaitForTimersAsync(Enumerable.Repeat(TimeSpan.FromSeconds(15), 4)
                .Concat(Enumerable.Repeat(TimeSpan.FromSeconds(1.5), 4)).ToArray());
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await sources[4].WaitForCallsAsync(4);
            Assert.Equal(20, sources.Sum(source => source.Calls));
            Assert.Equal(20, sources.Sum(source => source.ActualCalls.Count));
            foreach (var cancellation in cancellations) cancellation.Cancel();
            foreach (var lookup in pending) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.All(sources.SelectMany(source => source.ActualCalls), call => Assert.False(call.Task.IsCompleted));
            Assert.Null(await fixture.FindAsync());
            Assert.Equal(20, sources.Sum(source => source.Calls));
        }
        finally
        {
            foreach (var cancellation in cancellations) { cancellation.Cancel(); cancellation.Dispose(); }
            foreach (var source in sources) source.CompleteAll();
            await fixture.Resolver.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    public async Task OwnedCancellationDeadlineIsFifteenSecondsOrTheShorterOriginalDeadline(int seconds)
    {
        var source = new Source("lrclib");
        await using var fixture = new Fixture(source);
        try
        {
            var pending = fixture.FindAsync(deadline: TimeSpan.FromSeconds(seconds == 2 ? 2 : 30));
            await StartAsync(pending, source);
            await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.5));
            fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(Assert.Single(source.Tokens).IsCancellationRequested);
            Assert.False(source.Completion.Task.IsCompleted);
        }
        finally { source.CompleteAll(); await fixture.Resolver.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ResolverStopCancelsForegroundAndHonorsItsBoundedDrainToken()
    {
        var source = new Source("lrclib");
        await using var fixture = new Fixture(source);
        try
        {
            var pending = fixture.FindAsync();
            await StartAsync(pending, source);
            using var stopToken = new CancellationTokenSource(TimeSpan.FromSeconds(1), fixture.Clock);
            var stop = fixture.Resolver.StopAsync(stopToken.Token);
            await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(1));
            Assert.True(Assert.Single(source.Tokens).IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(source.Completion.Task.IsCompleted);
        }
        finally { source.CompleteAll(); await fixture.Resolver.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CompletedHeldAnswerIsDiscardedAfterAccountRevisionChanges()
    {
        var higher = new Source("lrclib");
        var lower = new Source("lrcred", Result("lrcred", true));
        await using var fixture = new Fixture(higher, lower) { UseAccount = true };
        var pending = fixture.FindAsync();
        await StartAsync(pending, lower);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        fixture.AccountRevision++;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        higher.Completion.SetResult(null);
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, lower.Calls);
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task SuccessfulLateHttpCompletionIsReusedFromTheExistingAdapterCache()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var root = Path.Combine(Path.GetTempPath(), "lyrics-cache-" + Guid.NewGuid().ToString("N"));
        using var cache = new ObservedCache(new FileMediaApplicationCache(new FileMediaCacheOptions(root), new ClockBridge(clock),
            NullLogger<FileMediaApplicationCache>.Instance), root);
        var handler = new WaitingHandler();
        var realSource = new LrcRedCapabilityAdapter(new HttpClient(handler), cache, timeProvider: clock);
        var lower = new Source("lrclib", Result("lrclib", false));
        await using var fixture = new Fixture(clock, realSource, lower);
        var pending = fixture.FindAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StartAsync(pending, lower);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal("lrclib", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        handler.Response.SetResult(new(HttpStatusCode.OK)
        {
            Content = new StringContent(
            "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"1s\" end=\"2s\"><span begin=\"1s\" end=\"1.5s\">Synthetic </span><span begin=\"1.5s\" end=\"2s\">words</span></p></div></body></tt>")
        });
        await cache.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cached = await fixture.FindAsync(new ProtocolLyricsSources { OnlySourceId = "lrcred" });
        Assert.Equal("lrcred", cached?.Source);
        Assert.Contains("Synthetic words", cached?.SyncedLyrics);
        Assert.NotNull(cached?.Revision);
        var line = Assert.Single(cached!.TimedLines!);
        Assert.Equal(TimeSpan.FromSeconds(1), line.Start);
        Assert.Equal(2, line.Segments!.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.5), line.Segments[1].Start);
        Assert.Equal(1, handler.Calls);
        Assert.Contains(ProviderRoutePurpose.ManualSelection, fixture.Plans);
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task SensitiveFallbackWaitsForActualSpeculativeWorkToQuiesce()
    {
        var first = new Source("lrclib");
        var sensitive = new Source("unison", Result("unison", true)) { Mode = LyricsSpeculationMode.RecentHealthy };
        await using var fixture = new Fixture(first, sensitive);
        var pending = fixture.FindAsync();
        await StartAsync(pending, first);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, sensitive.Calls);
        first.Completion.SetResult(null);
        Assert.Equal("unison", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.Equal(1, sensitive.Calls);
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task HeldAnswerIsDiscardedWhenAutomaticAvailabilityChanges()
    {
        var higher = new Source("lrclib");
        var lower = new Source("lrcred", Result("lrcred", true));
        await using var fixture = new Fixture(higher, lower);
        var pending = fixture.FindAsync();
        await StartAsync(pending, lower);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        fixture.CurrentPolicy = fixture.CurrentPolicy with
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("lrcred", ProviderUseMode.ManualOnly) };
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        higher.Completion.SetResult(null);
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, lower.Calls);
    }

    [Fact]
    public async Task HeldAnswerIsDiscardedAfterItsSourceConfigurationChanges()
    {
        var higher = new Source("lrclib");
        var lower = new Source("lrcred", Result("lrcred", true));
        await using var fixture = new Fixture(higher, lower);
        var pending = fixture.FindAsync();
        await StartAsync(pending, lower);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.5));
        lower.Version++;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        higher.Completion.SetResult(null);
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, lower.Calls);
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task RecentHealthIsExactToTheSelectedAccountRevisionConfigurationAndTtl()
    {
        var source = new Source("musixmatch") { Mode = LyricsSpeculationMode.RecentHealthy };
        await using var fixture = new Fixture(source) { UseAccount = true };
        var target = new ProviderExternalResourceId(source.ProviderId, ProviderResourceKind.Track, "track");
        var spec = new LyricsReadSpec(fixture.Protocol.Actor!, source.ProviderId, target, new(Guid.NewGuid(), target),
            [ProviderAccountScope.Shared], "health", fixture.Clock.GetUtcNow().AddHours(1));
        var gateway = (IProtocolLyricsExecutionGateway)fixture.Gateway;
        async Task<bool> Admitted() => (await gateway.PrepareAsync(spec, default))!.MaySpeculate;
        void Observe(Guid account, long revision, ProviderError? error = null) => ((ILyricsOutcomeObserver)fixture.Health).ObserveLyrics(
            new(fixture.Protocol.Actor!, source.ProviderId, new(account, source.ProviderId, ProviderAccountScope.Shared, revision),
                Context(source.ProviderId, default).Policy, "lyrics", "health", spec.Deadline, default), error, TimeSpan.FromMilliseconds(1), source.Snapshot.ConfigurationId);
        Assert.False(await Admitted());
        Observe(Guid.NewGuid(), 1);
        Assert.False(await Admitted());
        Observe(fixture.Account, 1);
        Assert.True(await Admitted());
        fixture.AccountRevision = 2;
        Assert.False(await Admitted());
        Observe(fixture.Account, 2);
        Assert.True(await Admitted());
        source.Version++;
        Assert.False(await Admitted());
        Observe(fixture.Account, 2, new(ProviderErrorKind.TransientFailure));
        Assert.False(await Admitted());
        Observe(fixture.Account, 2);
        Assert.True(await Admitted());
        Observe(fixture.Account, 2, new(ProviderErrorKind.TransientFailure));
        var key = ProviderRuntimeStatusKey.CreateManaged(source.ProviderId, ProviderCapabilities.Lyrics, fixture.Account);
        Assert.Equal(ProviderHealthState.Healthy, fixture.Health.GetLyrics(key, 2, source.Snapshot.ConfigurationId)?.Health);
        Assert.False(await Admitted());
        Observe(fixture.Account, 2);
        var probe = fixture.Health.BeginProbe(key, 2);
        Assert.False(await Admitted());
        fixture.Health.CancelProbe(probe);
        Assert.True(await Admitted());
        Observe(fixture.Account, 2, new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)));
        Assert.False(await Admitted());
        Observe(fixture.Account, 2);
        Assert.True(await Admitted());
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(await Admitted());
        await fixture.Resolver.StopAsync(default);
    }

    [Fact]
    public async Task LrclibCancellationStopsTheActualSearchWithoutStartingExactFallback()
    {
        var handler = new WaitingHandler();
        using var http = new HttpClient(handler);
        using var cache = new MemoryApplicationCache(new ClockBridge(TimeProvider.System));
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        clients.Setup(item => item.CreateClient("Lrclib")).Returns(http);
        var service = new LrclibService(clients.Object, cache, Options.Create(new CacheSettings()), NullLogger<LrclibService>.Instance);
        var adapter = BuiltInLyricsCapabilityRegistration.CreateLrclib(service);
        var source = (IInternalLyricsCapability)adapter;
        using var cancellation = new CancellationTokenSource();
        var pending = source.FetchAsync(Context("lrclib", cancellation.Token), new(Guid.NewGuid(), new("lrclib", ProviderResourceKind.Track, "track"),
            trackTitle: "Fixture", artistNames: ["Artist"], albumTitle: "Album", durationSeconds: 120),
            new(LyricsCompletionMode.OwnedAutomatic, await source.CaptureSourceAsync(default)));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(ProviderErrorKind.Canceled, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Error?.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OwnedTokenJoinDrainsActualSharedFetchWithoutCancelingItsPeer()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var handler = new WaitingHandler();
        using var http = new HttpClient(handler);
        var tokens = new MusixmatchTokenProvider(http, clock);
        using var cancellation = new CancellationTokenSource();
        var owned = tokens.GetAsync(null, cancellation.Token, LyricsCompletionMode.OwnedAutomatic);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var peer = tokens.GetAsync(null, default);
        cancellation.Cancel();
        Assert.False(owned.IsCompleted);
        Assert.False(peer.IsCompleted);
        handler.Response.SetResult(new(HttpStatusCode.OK)
        {
            Content = new StringContent(
            "{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"user_token\":\"synthetic-token\"}}}")
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owned);
        Assert.True((await peer).IsSuccess);
        Assert.True((await tokens.GetAsync(null, default)).IsSuccess);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SharedTokenHttpAcquisitionObservesHostShutdown()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var handler = new WaitingHandler();
        using var http = new HttpClient(handler);
        using var shutdown = new CancellationTokenSource();
        var tokens = new MusixmatchTokenProvider(http, clock, shutdown.Token);
        var pending = tokens.GetAsync(null, default, LyricsCompletionMode.OwnedAutomatic);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldConfigurationCompletionCannotOverwriteNewHealth(bool failure)
    {
        var source = new Source("lyricsplus");
        var health = new ProviderRuntimeHealth();
        var observed = ObservedProviderCapabilities.Wrap<IProviderLyricsCapability>(source, health);
        var context = Context(source.ProviderId, default);
        var request = new ProviderLyricsRequest(Guid.NewGuid(), new(source.ProviderId, ProviderResourceKind.Track, "track"));
        var pending = observed.FetchLyricsAsync(context, request);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Version++;
        source.Immediate = Result(source.ProviderId, true);
        Assert.True((await observed.FetchLyricsAsync(context, request)).IsSuccess);
        var key = ProviderRuntimeStatusKey.CreateAccountFree(source.ProviderId, ProviderCapabilities.Lyrics);
        var current = health.GetLyrics(key, null, source.Snapshot.ConfigurationId);
        Assert.NotNull(current);
        source.Completion.SetResult(failure ? ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30))) : Result(source.ProviderId, true));
        await pending;
        Assert.Same(current, health.GetLyrics(key, null, source.Snapshot.ConfigurationId));
        Assert.Equal(0, current.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheCancellationKeepsOwnedInnerWorkButPreservesRequestBoundCancellation(bool owned)
    {
        var mode = owned ? LyricsCompletionMode.OwnedAutomatic : LyricsCompletionMode.RequestBound;
        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        var read = new TaskCompletionSource<ProviderLyricsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.IsAny<string>())).Returns(read.Task);
        var adapter = new CacheSource(cache.Object);
        using var cancellation = new CancellationTokenSource();
        var context = Context(adapter.ProviderId, cancellation.Token);
        var source = (IInternalLyricsCapability)adapter;
        var pending = source.FetchAsync(context, new(Guid.NewGuid(), new(adapter.ProviderId, ProviderResourceKind.Track, "track")),
            new(mode, await source.CaptureSourceAsync(default)));
        cancellation.Cancel();
        if (mode == LyricsCompletionMode.OwnedAutomatic) Assert.False(pending.IsCompleted);
        else Assert.Equal(ProviderErrorKind.Canceled, (await pending).Error?.Kind);
        read.SetResult(null);
        Assert.Equal(ProviderErrorKind.Canceled, (await pending).Error?.Kind);
        Assert.Equal(0, adapter.Requests);
        cache.VerifyAll();
    }

    private static async Task StartAsync(Task<allstarr.Models.Lyrics.LyricsInfo?> pending, Source source)
    {
        await Task.WhenAny(pending, source.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (!source.Started.Task.IsCompleted)
        {
            var answer = await pending;
            Assert.Fail("Lookup ended before source start: " + (answer?.Source ?? "unavailable"));
        }
    }

    private sealed class CacheSource(IApplicationCache cache) : PublicLyricsCapabilityAdapter(new HttpClient(), cache, null)
    {
        public override string ProviderId => "lrcred";
        internal int Requests;
        protected override Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(ProviderLyricsRequest request, CancellationToken token)
        { Requests++; return Task.FromResult(Result(ProviderId, true)!); }
    }

    private sealed class Source(string id, ProviderOutcome<ProviderLyricsResult>? immediate = null) : IProviderLyricsCapability, IInternalLyricsCapability
    {
        public string ProviderId => id;
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;
        internal int Calls;
        internal long Version = 1;
        internal Exception? CaptureError;
        internal ConcurrentQueue<ProviderLyricsRequest> Requests = new();
        internal bool PerCall;
        internal ConcurrentQueue<TaskCompletionSource<ProviderOutcome<ProviderLyricsResult>?>> ActualCalls = new();
        internal ConcurrentQueue<CancellationToken> Tokens = new();
        internal void CompleteAll()
        {
            Completion.TrySetResult(null);
            foreach (var call in ActualCalls) call.TrySetResult(null);
        }
        internal LyricsSpeculationMode Mode = LyricsSpeculationMode.Unrestricted;
        internal ProviderOutcome<ProviderLyricsResult>? Immediate = immediate;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ProviderOutcome<ProviderLyricsResult>?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<TaskCompletionSource> waiters = new();
        internal LyricsSourceSnapshot Snapshot => new StaticLyricsSourceSnapshot(LyricsConfigurationId.From(id + ":" + Version, Version), Mode);
        public ValueTask<LyricsSourceSnapshot> CaptureSourceAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (CaptureError != null) throw CaptureError; return ValueTask.FromResult(Snapshot); }
        public async Task<ProviderOutcome<ProviderLyricsResult>> FetchAsync(ProviderExecutionContext context, ProviderLyricsRequest request, LyricsExecution execution)
        {
            var actual = PerCall ? new TaskCompletionSource<ProviderOutcome<ProviderLyricsResult>?>(TaskCreationOptions.RunContinuationsAsynchronously) : Completion;
            ActualCalls.Enqueue(actual);
            Tokens.Enqueue(context.CancellationToken);
            Requests.Enqueue(request);
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            foreach (var waiter in waiters) waiter.TrySetResult();
            return Immediate ?? await actual.Task ?? ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, id));
        }
        public Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(ProviderExecutionContext context, ProviderLyricsRequest request) =>
            FetchAsync(context, request, new(LyricsCompletionMode.RequestBound, Snapshot));
        internal async Task WaitForCallsAsync(int count)
        {
            while (Volatile.Read(ref Calls) < count)
            {
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Enqueue(waiter);
                if (Volatile.Read(ref Calls) >= count) return;
                await waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IProviderLyricsCapability[] _sources;
        internal readonly ManualTimeProvider Clock;
        internal readonly ConcurrentQueue<ProviderRoutePurpose> Plans = new();
        internal string? ExternalProvider, ExternalId, SpotifyId;
        internal Guid? Recording;
        internal bool RejectRecordingTarget;
        internal readonly ProtocolLyricsResolver Resolver;
        internal long AccountRevision = 1;
        internal bool UseAccount;
        internal readonly Guid Account = Guid.NewGuid();
        internal readonly ProtocolExecutionContext Protocol;
        internal readonly ProtocolProviderGateway Gateway;
        internal readonly ProviderRuntimeHealth Health;
        internal EffectiveProviderPolicySnapshot CurrentPolicy;
        internal Fixture(params IProviderLyricsCapability[] sources) : this(new ManualTimeProvider(DateTimeOffset.UtcNow), null, null, sources) { }
        internal Fixture(ICrossProviderLinkResolver links, params IProviderLyricsCapability[] sources) : this(new ManualTimeProvider(DateTimeOffset.UtcNow), links, null, sources) { }
        internal Fixture(ManualTimeProvider clock, params IProviderLyricsCapability[] sources) : this(clock, null, null, sources) { }
        internal Fixture(ITrackIdentityService identities, ICrossProviderLinkResolver links, params IProviderLyricsCapability[] sources)
            : this(new ManualTimeProvider(DateTimeOffset.UtcNow), links, identities, sources) { }
        private Fixture(ManualTimeProvider clock, ICrossProviderLinkResolver? links, ITrackIdentityService? identities, params IProviderLyricsCapability[] sources)
        {
            Clock = clock;
            _sources = sources;
            CurrentPolicy = new(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics,
                sources.Select(source => source.ProviderId).ToImmutableArray()), ImmutableHashSet<string>.Empty, "lossless", 0.07);
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(CurrentPolicy));
            policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(CurrentPolicy));
            var registry = new ProviderRegistry(sources.Select(source => new ProviderRegistration(new ProviderDescriptor(source.ProviderId,
                source.ProviderId, "Fixture lyrics", ProviderOrigin.BuiltIn, "1", "1",
                [new(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["fetchLyrics"])], new()), [source])));
            var health = Health = new ProviderRuntimeHealth(clock: new ClockBridge(Clock));
            var router = new Mock<IProviderRouter>(MockBehavior.Strict);
            router.Setup(item => item.PlanAsync<IProviderLyricsCapability>(It.IsAny<ProviderRouteRequest>())).Returns<ProviderRouteRequest>(request =>
            {
                Plans.Enqueue(request.Purpose);
                var id = Assert.Single(request.ProviderPriority);
                if (RejectRecordingTarget && request.SourceTrackId?.ProviderId == "allstarr")
                    return Task.FromResult(new ProviderRoutePlan<IProviderLyricsCapability>(request, [],
                        new(request.CorrelationId, request.Capability, null, null, [])));
                var source = sources.Single(source => source.ProviderId == id);
                var descriptor = registry.GetRequired(id);
                var context = new ProviderExecutionContext(request.Actor, id, UseAccount ? new ProviderAccountContext(Account, id, ProviderAccountScope.Shared, AccountRevision, secretReferenceId: id == "spicylyrics" ? SpicyLyricsTestData.SecretId : null) : null,
                    request.Policy, request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
                return Task.FromResult(new ProviderRoutePlan<IProviderLyricsCapability>(request,
                    [new(0, descriptor, Assert.Single(descriptor.Capabilities), ObservedProviderCapabilities.Wrap<IProviderLyricsCapability>(source, health), context, request.SourceTrackId?.ProviderId == "allstarr" ? new(id, ProviderResourceKind.Track, "local-recording-target") : request.SourceTrackId)],
                    new(request.CorrelationId, request.Capability, id, null, [])));
            });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Providers:LyricsOrder"] = string.Join(',', sources.Select(source => source.ProviderId)) }).Build();
            var gateway = Gateway = new ProtocolProviderGateway(router.Object, registry, Mock.Of<IHttpClientFactory>(), configuration, effectivePolicies: policies.Object, outcomes: health, timeProvider: Clock);
            Resolver = new(gateway, NullLogger<ProtocolLyricsResolver>.Instance, crossLinks: links, effectivePolicies: policies.Object, timeProvider: Clock, identities: identities);
            Protocol = new(ProtocolKind.Jellyfin, "fixture", "listener", new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "fixture", "listener", "Listener", false),
                "lyrics", Clock.GetUtcNow().AddMinutes(1), default);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var source in _sources.OfType<Source>()) source.CompleteAll();
            await Resolver.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        internal Task<allstarr.Models.Lyrics.LyricsInfo?> FindAsync(ProtocolLyricsSources? sources = null, CancellationToken token = default, TimeSpan? deadline = null) => Resolver.FindAsync(
            new(Protocol.Protocol, Protocol.BackendInstanceId, Protocol.VerifiedBackendPrincipalId, Protocol.Principal, Protocol.CorrelationId,
                deadline.HasValue ? Clock.GetUtcNow().Add(deadline.Value) : Protocol.Deadline, token),
            new Song { Title = "Fixture", Artist = "Artist", Artists = ["Artist"], Album = "Album", Isrc = "USFIX0000001", CanonicalRecordingId = Recording, ExternalProvider = ExternalProvider, ExternalId = ExternalId, SpotifyId = SpotifyId }, "recording", sources ?? new ProtocolLyricsSources());
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        internal int Calls;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<HttpResponseMessage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); Started.TrySetResult(); return await Response.Task.WaitAsync(token); }
    }
    private sealed class ObservedCache(FileMediaApplicationCache inner, string root) : IApplicationCache, IDisposable
    {
        internal TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsEnabled => inner.IsEnabled;
        public Task<string?> GetStringAsync(string key) => inner.GetStringAsync(key);
        public Task<T?> GetAsync<T>(string key) where T : class => inner.GetAsync<T>(key);
        public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null) => inner.SetStringAsync(key, value, expiry);
        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class
        { var result = await inner.SetAsync(key, value, expiry); Written.TrySetResult(); return result; }
        public Task<bool> DeleteAsync(string key) => inner.DeleteAsync(key);
        public Task<bool> ExistsAsync(string key) => inner.ExistsAsync(key);
        public Task<int> DeleteByPatternAsync(string pattern) => inner.DeleteByPatternAsync(pattern);
        public Task<int> PurgeAllAsync() => inner.PurgeAllAsync();
        public void Dispose() { inner.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class ClockBridge(TimeProvider clock) : IPlatformClock
    { public DateTimeOffset UtcNow => clock.GetUtcNow(); }
    private static ProviderExecutionContext Context(string provider, CancellationToken token) => new(
        new(ProviderActorKind.User, Guid.NewGuid(), new("jellyfin", "fixture", "listener")), provider, null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow, true, true, false, [provider]),
        "lyrics", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), token);
    private static ProviderOutcome<ProviderLyricsResult> Result(string provider, bool timed) => ProviderOutcome<ProviderLyricsResult>.Success(new(
        ProviderLyricsAvailabilityState.Available, provider, timed ? ProviderLyricsFormat.LineTimed : ProviderLyricsFormat.PlainText,
        timed ? "[00:01]Words" : "Words"));
}
