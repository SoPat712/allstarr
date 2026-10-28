using System.Net;
using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Health;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Downloads;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Models.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task MonochromeProtectedOpen_PreservesRetryAfterForRouting()
    {
        var first = Streaming("monochrome", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(47));
            return Task.FromResult(response);
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(first, second);
        using var opened = (await gateway.OpenStreamAsync(Context(), "monochrome", "155142501534011392", ProviderAudioQuality.Any, null))!.Response;
        router.Verify(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(), 0,
            It.Is<ProviderError>(error => error.Kind == ProviderErrorKind.RateLimited && error.RetryAfter == TimeSpan.FromSeconds(47))), Times.Once);
    }

    [Theory]
    [InlineData("missing", ProviderErrorKind.NotFound)]
    [InlineData("server", ProviderErrorKind.TransientFailure)]
    [InlineData("rate", ProviderErrorKind.RateLimited)]
    [InlineData("transport", ProviderErrorKind.TransientFailure)]
    [InlineData("timeout", ProviderErrorKind.TransientFailure)]
    [InlineData("body", ProviderErrorKind.TransientFailure)]
    [InlineData("empty", ProviderErrorKind.IncompatibleMedia)]
    [InlineData("html", ProviderErrorKind.IncompatibleMedia)]
    public async Task OpenStream_FallsThroughBeforeCommittingMedia(string failure, ProviderErrorKind kind)
    {
        var content = new ObservedContent(failure == "empty" ? [] : [8, 9]);
        if (failure == "html") content.Headers.ContentType = new("text/html");
        var failed = new HttpResponseMessage(failure switch
        {
            "missing" => HttpStatusCode.NotFound,
            "server" => HttpStatusCode.BadGateway,
            "rate" => HttpStatusCode.TooManyRequests,
            _ => HttpStatusCode.OK
        })
        { Content = content };
        var first = Streaming("deezer", (_, _) => failure switch
        {
            "transport" => throw new HttpRequestException("private provider URL must not escape"),
            "timeout" => throw new TaskCanceledException(),
            "body" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new BrokenStream()) }),
            _ => Task.FromResult(failed)
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(first, second);
        var opened = await gateway.OpenStreamAsync(Context(), "deezer", "source-track", ProviderAudioQuality.Any, null);

        Assert.NotNull(opened);
        using var response = opened.Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        Assert.Equal("qobuz-track", opened.ServingExternalId);
        Assert.StartsWith("fallback-", opened.SelectionReason, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        Assert.Equal(opened.SelectionReason,
            Assert.Single(response.Headers.GetValues("X-Allstarr-Route-Reason")));
        router.Verify(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(), 0,
            It.Is<ProviderError>(error => error.Kind == kind)), Times.Once);
        if (failure is not ("transport" or "timeout" or "body")) Assert.True(content.Disposed);
        failed.Dispose();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task OpenStream_DoesNotBypassAuthenticationFailures(HttpStatusCode status)
    {
        var content = new ObservedContent([1]);
        var first = Streaming("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content }));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.OpenStreamAsync(Context(), "deezer", "source-track", ProviderAudioQuality.Any, null));
        Assert.True(content.Disposed);
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_UsesConfiguredOrderInsteadOfTheCatalogProvider()
    {
        var preferred = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var catalog = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(preferred, catalog);
        var opened = await gateway.OpenStreamAsync(Context(), "deezer", "source-track", ProviderAudioQuality.Any, null);
        using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.Is<ProviderRouteRequest>(request =>
            request.ProviderPriority.SequenceEqual(new[] { "qobuz", "deezer" }))), Times.Once);
        catalog.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_DoesNotLetALowerPriorityCacheHitJumpTheProviderOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cachedPath = Path.Combine(root, "cached.flac");
        await File.WriteAllBytesAsync(cachedPath, [9, 9, 9]);
        try
        {
            var context = Context();
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(
                    It.Is<DownloadedSongMappingScope>(scope => scope.ProviderAccountId == null),
                    "qobuz",
                    "qobuz-track"))
                .ReturnsAsync((string?)null);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(
                    It.IsAny<DownloadedSongMappingScope>(),
                    "deezer",
                    "source-track"))
                .ReturnsAsync(cachedPath);
            var cache = new ManagedTrackCacheService(
                new ConfigurationBuilder().Build(),
                Options.Create(new SubsonicSettings()),
                local.Object,
                NullLogger<ManagedTrackCacheService>.Instance, audioVerifier: FixtureAudioVerifier.Instance);
            var preferred = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
            var cachedFallback = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
            var (gateway, _) = FailoverGateway(
                preferred,
                cachedFallback,
                managedTrackCache: cache);

            var opened = await gateway.OpenStreamAsync(
                context, "deezer", "source-track", ProviderAudioQuality.Any, null);

            using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
            Assert.Equal("qobuz", opened.ServingProviderId);
            Assert.False(opened.IsCached);
            Assert.Equal("provider-priority:account-free:remote", opened.SelectionReason);
            local.Verify(item => item.GetLocalPathForExternalSongAsync(
                It.IsAny<DownloadedSongMappingScope>(), "deezer", "source-track"), Times.Never);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenStream_UsesHouseholdPolicyInsteadOfProcessConfiguration()
    {
        var configuredFirst = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var householdFirst = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(
            configuredFirst,
            householdFirst,
            householdStreamingOrder: ["qobuz", "deezer"]);

        var opened = await gateway.OpenStreamAsync(
            Context(),
            "deezer",
            "source-track",
            ProviderAudioQuality.Any,
            null);

        using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(
            It.Is<ProviderRouteRequest>(request =>
                request.ProviderPriority.SequenceEqual(new[] { "qobuz", "deezer" }))), Times.Once);
    }

    [Fact]
    public async Task OpenStream_UsesHouseholdQualityWhenTheClientDoesNotSetALowerCap()
    {
        var preferred = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var fallback = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(
            preferred,
            fallback,
            householdStreamingOrder: ["qobuz", "deezer"],
            householdAudioQuality: "High");

        using var response = (await gateway.OpenStreamAsync(
            Context(),
            "deezer",
            "source-track",
            ProviderAudioQuality.Any,
            null))!.Response;

        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(
            It.Is<ProviderRouteRequest>(request =>
                request.Policy.Quality.Maximum == ProviderAudioQuality.Lossy)), Times.Once);
        preferred.Verify(item => item.GetStreamLeaseAsync(
            It.IsAny<ProviderExecutionContext>(),
            It.Is<ProviderStreamLeaseRequest>(request =>
                request.RequestedQuality == ProviderAudioQuality.Lossy)), Times.Once);
    }

    [Fact]
    public async Task OpenStream_ExcludesProvidersDisabledForTheHousehold()
    {
        var disabled = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var available = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(
            disabled,
            available,
            householdStreamingOrder: ["qobuz", "deezer"],
            householdDisabledProviders: ["qobuz"]);

        var opened = await gateway.OpenStreamAsync(
            Context(),
            "deezer",
            "source-track",
            ProviderAudioQuality.Any,
            null);

        using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
        Assert.Equal("deezer", opened.ServingProviderId);
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(
            It.Is<ProviderRouteRequest>(request =>
                request.ProviderPriority.SequenceEqual(new[] { "deezer" }))), Times.Once);
        disabled.Verify(item => item.GetStreamLeaseAsync(
            It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_OnlyOneSameSourceRetryBeforeFailover()
    {
        var attempts = 0;
        var first = Streaming("deezer", (_, _) =>
        {
            attempts++;
            throw new HttpRequestException();
        }, ProviderStreamRetryBehavior.RetrySameLeaseOnce);
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        using var response = (await gateway.OpenStreamAsync(Context(), "deezer", "source-track",
            ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal(2, attempts);
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    [Fact]
    public async Task OpenStream_CancellationStopsWithoutTryingAnotherProvider()
    {
        using var cancelled = new CancellationTokenSource();
        var first = Streaming("deezer", (_, _) => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        var context = ClientContext(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null));
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_ResumeUsesPreviousEncodingAndHeadDoesNotOverwriteIt()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var context = ClientContext();
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.NotFound)));
        string? observedRange = null;
        var second = Streaming("qobuz", (request, _) =>
        {
            observedRange = request.Headers.Range?.ToString();
            var response = AudioResponse();
            if (request.Headers.Range != null)
            {
                response.StatusCode = HttpStatusCode.PartialContent;
                response.Content = new ByteArrayContent([2, 3, 4]);
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(1, 3, 4);
            }
            return Task.FromResult(response);
        });
        var (gateway, router) = FailoverGateway(first, second, activity);
        using var firstResponse = (await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null))!.Response;
        var source = activity.StreamFor(context, "ext-deezer-song-source-track", ProviderAudioQuality.Any);
        Assert.Equal("qobuz", source!.ProviderId);

        using var resumed = (await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, "bytes=1-"))!.Response;
        Assert.Equal("bytes=1-", observedRange);
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.Is<ProviderRouteRequest>(request =>
            request.ProviderPriority.SequenceEqual(new[] { "qobuz", "deezer" }))), Times.Once);

        var lastOpened = activity.StreamFor(context, "ext-deezer-song-source-track", ProviderAudioQuality.Any);
        using var head = (await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null, headOnly: true))!.Response;
        Assert.Same(lastOpened, activity.StreamFor(context, "ext-deezer-song-source-track", ProviderAudioQuality.Any));
    }

    [Fact]
    public async Task OpenStream_DeniedAccountCannotReadTheWarmCache()
    {
        var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
        var cache = new ManagedTrackCacheService(new ConfigurationBuilder().Build(),
            Options.Create(new SubsonicSettings()), local.Object, NullLogger<ManagedTrackCacheService>.Instance, audioVerifier: FixtureAudioVerifier.Instance);
        var capability = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var registry = Registry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderStreamingCapability>(request, [],
                new ProviderRouteDecisionRecord(request.CorrelationId, ProviderCapabilityKind.Streaming, null, null,
                    [new ProviderRouteCandidateDecision("deezer", null, ProviderRouteDecisionStatus.Rejected, "account-scope-denied", 0)])));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), managedTrackCache: cache, audioVerifier: FixtureAudioVerifier.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenStreamAsync(Context(), "deezer", "source-track",
            ProviderAudioQuality.Any, null));
        local.VerifyNoOtherCalls();
        capability.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task PlaybackSourceIsScopedToUserDeviceBackendAndQuality()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var context = ClientContext();
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, activity);
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null))!.Response;
        const string itemId = "ext-deezer-song-source-track";
        Assert.NotNull(activity.StreamFor(context, itemId, ProviderAudioQuality.Any));
        var otherBackend = new ProtocolExecutionContext(context.Protocol, "other-backend", context.VerifiedBackendPrincipalId,
            context.Principal! with { BackendInstanceId = "other-backend" }, context.CorrelationId, context.Deadline, default, context.Client);
        Assert.Null(activity.StreamFor(otherBackend, itemId, ProviderAudioQuality.Any));
        Assert.Null(activity.StreamFor(context, itemId, ProviderAudioQuality.Lossy));
        Assert.Null(activity.StreamFor(Guid.CreateVersion7(), "device", itemId));
        Assert.Null(activity.StreamFor(context.Actor!.UserId, "other-device", itemId));
        Assert.Null(activity.StreamFor(context.Actor!.UserId, "device", "ext-deezer-song-another"));
    }

    [Fact]
    public async Task JellyfinSongInfoShowsOnlyThisListenersLastOpenedSource()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var context = ClientContext();
        var first = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, activity);
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = context;
        var adapter = new JellyfinItemProtocolAdapter(new JellyfinResponseBuilder(),
            new HttpContextAccessor { HttpContext = http }, activity);
        var song = new Song { Id = "ext-deezer-song-source-track", Title = "Track", ExternalProvider = "deezer", ExternalId = "source-track" };
        var result = Assert.IsType<JsonResult>(adapter.ShapeSong(song));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("Last stream: qobuz", json);
        Assert.DoesNotContain("https://media", json);
        Assert.Equal("private, no-store", http.Response.Headers.CacheControl);
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = ClientContext();
        var other = Assert.IsType<JsonResult>(adapter.ShapeSong(song));
        Assert.DoesNotContain("Last stream:", JsonSerializer.Serialize(other.Value));
    }

    [Fact]
    public async Task OpenStream_DeadlineCancelsAnUnresponsiveSource()
    {
        var first = Streaming("deezer", async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return AudioResponse(); });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        var context = ClientContext();
        context = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId,
            context.Principal, context.CorrelationId, DateTimeOffset.UtcNow.AddMilliseconds(100), default, context.Client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null));
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(null, 150)]
    [InlineData("240", 240)]
    public async Task OpenStream_GivesDownloadBackedSourcesTheirConfiguredDeadline(
        string? configuredSeconds,
        int expectedSeconds)
    {
        ProviderExecutionContext? observed = null;
        var download = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        download.As<IProviderDownloadStreamingCapability>();
        download.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()))
            .Callback<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) => observed = context)
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
                "lease", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(5), true, true,
                new ProviderMediaFormat("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry,
                (_, _) => Task.FromResult(AudioResponse()))));
        var direct = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var settings = new Dictionary<string, string?>
        {
            ["Providers:StreamingOrder"] = "deezer,qobuz"
        };
        if (configuredSeconds != null)
            settings["ProtocolExecution:DownloadStreamTimeoutSeconds"] = configuredSeconds;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var registry = Registry(download.Object, direct.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry, download.Object));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), configuration, audioVerifier: FixtureAudioVerifier.Instance);
        var before = DateTimeOffset.UtcNow;

        using var response = (await gateway.OpenStreamAsync(
            Context(), "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;

        Assert.NotNull(observed);
        Assert.InRange(observed.Deadline, before.AddSeconds(expectedSeconds - 2), before.AddSeconds(expectedSeconds + 2));
    }

    [Fact]
    public async Task OpenStream_KeepsTheProtocolDeadlineForDirectSources()
    {
        ProviderExecutionContext? observed = null;
        var direct = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        direct.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()))
            .Callback<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) => observed = context)
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
                "lease", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(5), true, true,
                new ProviderMediaFormat("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry,
                (_, _) => Task.FromResult(AudioResponse()))));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(direct, second);
        var context = Context();

        using var response = (await gateway.OpenStreamAsync(
            context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;

        Assert.NotNull(observed);
        Assert.Equal(context.Deadline, observed.Deadline);
    }

    [Fact]
    public async Task OpenStream_DoesNotCarryTheDownloadDeadlineIntoDirectFallback()
    {
        ProviderExecutionContext? downloadContext = null;
        ProviderExecutionContext? directContext = null;
        var download = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        download.As<IProviderDownloadStreamingCapability>();
        download.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Callback<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) => downloadContext = context)
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Failure(
                new ProviderError(ProviderErrorKind.TransientFailure)));
        var direct = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        direct.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Callback<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) => directContext = context)
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
                "lease", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(5), true, true,
                new ProviderMediaFormat("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry,
                (_, _) => Task.FromResult(AudioResponse()))));
        var (gateway, _) = FailoverGateway(download, direct);
        var context = Context();

        using var response = (await gateway.OpenStreamAsync(
            context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;

        Assert.NotNull(downloadContext);
        Assert.True(downloadContext.Deadline > context.Deadline);
        Assert.NotNull(directContext);
        Assert.Equal(context.Deadline, directContext.Deadline);
    }

    [Fact]
    public async Task OpenStream_UsesOneDeadlineAcrossDownloadFallbackCandidates()
    {
        DateTimeOffset? firstDeadline = null;
        DateTimeOffset? secondDeadline = null;
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.As<IProviderDownloadStreamingCapability>();
        first.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Callback<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) => firstDeadline = context.Deadline)
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Failure(
                new ProviderError(ProviderErrorKind.TransientFailure)));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        second.As<IProviderDownloadStreamingCapability>();
        second.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Callback<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) => secondDeadline = context.Deadline)
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
                "lease", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(5), true, true,
                new ProviderMediaFormat("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry,
                (_, _) => Task.FromResult(AudioResponse()))));
        var (gateway, _) = FailoverGateway(first, second);

        using var response = (await gateway.OpenStreamAsync(
            Context(), "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;

        Assert.NotNull(firstDeadline);
        Assert.Equal(firstDeadline, secondDeadline);
    }

    [Fact]
    public async Task OpenStream_DownloadDeadlineDoesNotMaskRequestCancellation()
    {
        using var request = new CancellationTokenSource();
        var download = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        download.As<IProviderDownloadStreamingCapability>();
        download.SetupGet(item => item.ProviderId).Returns("deezer");
        download.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        download.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns<ProviderExecutionContext, ProviderStreamLeaseRequest>(async (context, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                throw new InvalidOperationException();
            });
        var direct = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(download, direct);
        var opening = gateway.OpenStreamAsync(
            ClientContext(request.Token), "deezer", "source-track", ProviderAudioQuality.Any, null);

        request.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        direct.Verify(item => item.GetStreamLeaseAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_DoesNotReviveAnExpiredDownloadContext()
    {
        var download = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        download.As<IProviderDownloadStreamingCapability>();
        var direct = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(download, direct);
        var original = ClientContext();
        var expired = new ProtocolExecutionContext(
            original.Protocol, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
            original.Principal, original.CorrelationId, DateTimeOffset.UtcNow.AddSeconds(-1),
            original.CancellationToken, original.Client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.OpenStreamAsync(
            expired, "deezer", "source-track", ProviderAudioQuality.Any, null));

        download.Verify(item => item.GetStreamLeaseAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_DoesNotStartDirectFallbackAfterTheProtocolDeadlineExpires()
    {
        var download = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        download.As<IProviderDownloadStreamingCapability>();
        download.SetupGet(item => item.ProviderId).Returns("deezer");
        download.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        download.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(async () =>
            {
                await Task.Delay(100);
                return ProviderOutcome<ProviderStreamLease>.Failure(
                    new ProviderError(ProviderErrorKind.TransientFailure));
            });
        var direct = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(download, direct);
        var original = ClientContext();
        var shortContext = new ProtocolExecutionContext(
            original.Protocol, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
            original.Principal, original.CorrelationId, DateTimeOffset.UtcNow.AddMilliseconds(50),
            original.CancellationToken, original.Client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.OpenStreamAsync(
            shortContext, "deezer", "source-track", ProviderAudioQuality.Any, null));

        direct.Verify(item => item.GetStreamLeaseAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    private static HttpResponseMessage AudioResponse(HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new ByteArrayContent([1, 2, 3, 4])
    };

    private static Mock<IProviderStreamingCapability> Streaming(string provider,
        ProviderStreamResponseFactory open, ProviderStreamRetryBehavior retry = ProviderStreamRetryBehavior.DoNotRetry) =>
        Capability(provider, ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
            "lease", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1), true, true,
            new ProviderMediaFormat("audio/flac", "flac", "flac"), retry, open)));

    private static (ProtocolProviderGateway Gateway, Mock<IProviderRouter> Router) FailoverGateway(
        Mock<IProviderStreamingCapability> first, Mock<IProviderStreamingCapability> second,
        PlaybackDeliveryActivityStore? activity = null,
        IReadOnlyList<string>? householdStreamingOrder = null,
        IReadOnlyList<string>? householdDisabledProviders = null,
        string householdAudioQuality = AudioQualityPolicy.DefaultStep,
        ManagedTrackCacheService? managedTrackCache = null,
        IProviderOutcomeObserver? outcomes = null, TimeProvider? clock = null,
        allstarr.Core.Matching.ITrackIdentityService? identities = null,
        allstarr.Core.Music.IProviderAudioVerifier? audioVerifier = null)
    {
        var registry = Registry(first.Object, second.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                new[] { first.Object, second.Object }
                    .Where(item => request.ProviderPriority.Contains(item.ProviderId))
                    .OrderBy(item => request.ProviderPriority.ToList().IndexOf(item.ProviderId))
                    .ToArray()));
        router.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(),
                It.IsAny<int>(), It.IsAny<ProviderError>()))
            .Returns((ProviderRoutePlan<IProviderStreamingCapability> plan, int index, ProviderError error) =>
                new ProviderFallbackDecision<IProviderStreamingCapability>(
                    error.Kind is ProviderErrorKind.Unauthorized or ProviderErrorKind.Forbidden || index + 1 == plan.Candidates.Count
                        ? ProviderFallbackDisposition.StopFailure : ProviderFallbackDisposition.Advance,
                    error.Code, index + 1 < plan.Candidates.Count ? plan.Candidates[index + 1] : null));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:StreamingOrder"] = $"{first.Object.ProviderId},{second.Object.ProviderId}"
        }).Build();
        IEffectiveProviderPolicyResolver? policies = null;
        if (householdStreamingOrder != null)
        {
            var resolver = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
            resolver.Setup(item => item.ResolveForUserAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                    new Dictionary<ProviderCapabilityKind, System.Collections.Immutable.ImmutableArray<string>>
                    {
                        [ProviderCapabilityKind.Streaming] = householdStreamingOrder.ToImmutableArray()
                    }.ToImmutableDictionary(),
                    (householdDisabledProviders ?? []).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                    householdAudioQuality,
                    0.07));
            policies = resolver.Object;
        }
        return (new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), configuration,
            managedTrackCache: managedTrackCache, playbackActivity: activity, effectivePolicies: policies,
            outcomes: outcomes, timeProvider: clock, identities: identities, audioVerifier: audioVerifier ?? FixtureAudioVerifier.Instance), router);
    }

    private static ProtocolExecutionContext ClientContext(CancellationToken cancellationToken = default)
    {
        var original = Context();
        return new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
            original.Principal, original.CorrelationId, original.Deadline, cancellationToken,
            new ProtocolClientDescriptor("client", "device"));
    }

    private sealed class ObservedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("upstream media failed");
    }
}
