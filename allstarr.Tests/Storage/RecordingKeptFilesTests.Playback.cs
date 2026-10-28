using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task HouseholdKeptPlaybackWinsBeforeProviderAndNegativeCache(ProtocolKind protocol)
    {
        var published = await Service().PublishAsync(Request());
        await DisableSourceAccountAsync();
        await SetListenerBackendAsync(protocol);

        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        cache.SetupGet(item => item.IsEnabled).Returns(true);
        cache.Setup(item => item.ExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = KeptGateway(router.Object, cache.Object);
        var context = PlaybackContext(protocol, other, "ext-deezer-song-source-track");

        var stream = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"),
            ProviderAudioQuality.Any, null);

        Assert.NotNull(stream);
        Assert.Equal("allstarr", stream.ServingProviderId);
        Assert.Equal(recordingId.ToString("D"), stream.ServingExternalId);
        Assert.Equal(published.ManagedFileId, stream.ServingManagedFileId);
        Assert.True(stream.IsCached);
        var result = await ExecuteResponseAsync(stream.Response, HttpMethods.Get, null);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("audio/flac", result.ContentType);
        Assert.Equal(audio.Length, result.ContentLength);
        Assert.Equal(audio, result.Body);

        cache.Verify(item => item.ExistsAsync(It.IsAny<string>()), Times.Never);
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task KeptPlaybackUsesSeekableAdapterRangesAndHeadWithoutChangingSelection(ProtocolKind protocol)
    {
        var published = await Service().PublishAsync(Request());
        await SetListenerBackendAsync(protocol);
        using var activity = new PlaybackDeliveryActivityStore();
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = KeptGateway(router.Object, playbackActivity: activity);
        var itemId = "ext-deezer-song-source-track";
        var context = PlaybackContext(protocol, other, itemId);

        var initial = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"),
            ProviderAudioQuality.Any, "bytes=0-");
        Assert.NotNull(initial);
        Assert.Equal(published.ManagedFileId, initial.ServingManagedFileId);
        Assert.Equal(audio.Length, initial.Response.Content.Headers.ContentLength);
        Assert.Equal(HttpStatusCode.OK, initial.Response.StatusCode);
        Assert.Equal(audio, (await ExecuteResponseAsync(initial.Response, HttpMethods.Get, "bytes=0-")).Body);
        var source = activity.StreamFor(context, itemId, ProviderAudioQuality.Any);
        Assert.NotNull(source);
        Assert.Equal("allstarr", source!.ProviderId);
        Assert.Equal(recordingId.ToString("D"), source.ExternalId);
        Assert.Equal(published.ManagedFileId, source.ManagedFileId);

        var middle = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"),
            ProviderAudioQuality.Any, "bytes=5-9");
        Assert.NotNull(middle);
        var middleResult = await ExecuteResponseAsync(middle.Response, HttpMethods.Get, "bytes=5-9");
        Assert.Equal(StatusCodes.Status206PartialContent, middleResult.StatusCode);
        Assert.Equal(audio[5..10], middleResult.Body);
        Assert.Equal($"bytes 5-9/{audio.Length}", middleResult.ContentRange);

        var suffix = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"),
            ProviderAudioQuality.Any, "bytes=-5");
        Assert.NotNull(suffix);
        var suffixResult = await ExecuteResponseAsync(suffix.Response, HttpMethods.Get, "bytes=-5");
        Assert.Equal(StatusCodes.Status206PartialContent, suffixResult.StatusCode);
        Assert.Equal(audio[^5..], suffixResult.Body);

        var outside = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"),
            ProviderAudioQuality.Any, $"bytes={audio.Length}-");
        Assert.NotNull(outside);
        var outsideResult = await ExecuteResponseAsync(outside.Response, HttpMethods.Get, $"bytes={audio.Length}-");
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, outsideResult.StatusCode);

        var beforeHead = activity.StreamFor(context, itemId, ProviderAudioQuality.Any);
        var head = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"),
            ProviderAudioQuality.Any, "bytes=1-2", headOnly: true);
        Assert.NotNull(head);
        var headResult = await ExecuteResponseAsync(head.Response, HttpMethods.Head, "bytes=1-2");
        Assert.Equal(StatusCodes.Status200OK, headResult.StatusCode);
        Assert.Empty(headResult.Body);
        Assert.Equal(audio.Length, headResult.ContentLength);
        Assert.Equal(beforeHead, activity.StreamFor(context, itemId, ProviderAudioQuality.Any));
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "missing")]
    [InlineData(ProtocolKind.Subsonic, "missing")]
    [InlineData(ProtocolKind.Jellyfin, "corrupt")]
    [InlineData(ProtocolKind.Subsonic, "corrupt")]
    [InlineData(ProtocolKind.Jellyfin, "replaced")]
    [InlineData(ProtocolKind.Subsonic, "replaced")]
    public async Task KeptPlaybackSeekUsesAReplacementOrResolvesAFreshSource(ProtocolKind protocol, string change)
    {
        var published = await Service().PublishAsync(Request());
        await SetListenerBackendAsync(protocol);
        using var activity = new PlaybackDeliveryActivityStore();
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = KeptGateway(router.Object, playbackActivity: activity);
        var context = PlaybackContext(protocol, other, "ext-deezer-song-source-track");

        var initial = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"),
            ProviderAudioQuality.Any, null);
        Assert.NotNull(initial);
        initial.Response.Dispose();
        if (change == "missing") File.Delete(published.Path);
        else if (change == "corrupt") await File.WriteAllBytesAsync(published.Path, new byte[audio.Length]);
        else
        {
            await using var scope = services.CreateAsyncScope();
            var replacementRoot = Guid.CreateVersion7();
            var replacement = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
                new(replacementRoot, Path.Combine(root, "replacement"), null), artifact.SourcePath,
                "{title}", Request().Track, null, ManagedFileScopeKey.Create(null, replacementRoot), true,
                ExpectedContentSha256: artifact.ContentSha256, ExpectedLength: artifact.Length)
            { ReferenceKey = "replacement" });
            await using var db = new AllstarrDbContext(database.Options);
            (await db.Set<RecordingKeptRouteEntity>().SingleAsync()).ManagedFileId = replacement.File.Id;
            await db.SaveChangesAsync();
        }

        if (change != "replaced")
            router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) => EmptyStreamingPlan(request));
        var resumed = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"),
            ProviderAudioQuality.Any, "bytes=5-");
        if (change == "replaced")
        {
            Assert.NotNull(resumed);
            Assert.NotEqual(published.ManagedFileId, resumed.ServingManagedFileId);
            var result = await ExecuteResponseAsync(resumed.Response, HttpMethods.Get, "bytes=5-");
            Assert.Equal(StatusCodes.Status206PartialContent, result.StatusCode);
            Assert.Equal(audio[5..], result.Body);
            router.VerifyNoOtherCalls();
        }
        else
        {
            Assert.Null(resumed);
            router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()), Times.Once);
        }
    }

    [Fact]
    public async Task ReplayUsesTheCompleteKeptOriginalWhenItAppears()
    {
        var capability = RemoteCapability();
        var registry = RemoteRegistry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => RemotePlan(request, registry, capability.Object));
        using var activity = new PlaybackDeliveryActivityStore();
        var gateway = new ProtocolProviderGateway(router.Object, registry, new ThrowingHttpClientFactory(),
            playbackActivity: activity, identities: identities, keptFiles: Service(), audioVerifier: FixtureAudioVerifier.Instance);
        var itemId = "ext-deezer-song-source-track";
        var context = PlaybackContext(ProtocolKind.Jellyfin, owner, itemId);

        var first = await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null);
        Assert.NotNull(first);
        first.Response.Dispose();
        var firstSource = activity.StreamFor(context, itemId, ProviderAudioQuality.Any);
        Assert.NotNull(firstSource);
        Assert.Equal("deezer", firstSource!.ProviderId);
        Assert.Equal(account, firstSource.AccountId);
        Assert.Equal(7L, firstSource.AccountRevision);

        await Service().PublishAsync(Request());

        var resumed = await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, "bytes=5-");
        Assert.NotNull(resumed);
        Assert.Equal("allstarr", resumed.ServingProviderId);
        Assert.Equal(recordingId.ToString("D"), resumed.ServingExternalId);
        Assert.True(resumed.IsCached);
        Assert.NotNull(resumed.ServingManagedFileId);
        var result = await ExecuteResponseAsync(resumed.Response, HttpMethods.Get, "bytes=5-");
        Assert.Equal(StatusCodes.Status206PartialContent, result.StatusCode);
        Assert.Equal(audio[5..], result.Body);
        capability.Verify(item => item.GetStreamLeaseAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    [Theory]
    [InlineData(ProviderUseMode.Automatic, true, false)]
    [InlineData(ProviderUseMode.ManualOnly, false, true)]
    [InlineData(ProviderUseMode.Off, false, false)]
    public async Task ExplicitSourceUsesKeptPlaybackOnlyWhenAutomatic(
        ProviderUseMode mode, bool expectedKept, bool expectedProviderPlan)
    {
        var published = await Service().PublishAsync(Request());
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        if (expectedProviderPlan)
        {
            router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) => EmptyStreamingPlan(request));
            router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderMetadataCapability>(
                    request, [], new(request.CorrelationId, request.Capability, null, null, [])));
        }
        var policies = PlaybackPolicy("deezer", mode);
        var gateway = KeptGateway(router.Object, effectivePolicies: policies.Object);
        var context = PlaybackContext(ProtocolKind.Jellyfin, other, "ext-deezer-song-source-track");

        if (mode == ProviderUseMode.Off)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenStreamAsync(
                context, "deezer", "source-track", ProviderAudioQuality.Any, null));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.GetSongAsync(context, "deezer", "source-track"));
            router.VerifyNoOtherCalls();
            return;
        }
        var metadata = await gateway.GetSongAsync(context, "deezer", "source-track");
        Assert.Equal(expectedKept, metadata != null);
        var stream = await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null);

        if (expectedKept)
        {
            Assert.NotNull(stream);
            Assert.Equal("allstarr", stream.ServingProviderId);
            Assert.Equal(published.ManagedFileId, stream.ServingManagedFileId);
            stream.Response.Dispose();
        }
        else
        {
            Assert.Null(stream);
        }
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()),
            expectedProviderPlan ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task LegacyProviderPlaybackWithoutDeviceKeepsOneEncodingAcrossRanges()
    {
        await Service().PublishAsync(Request());
        var capability = RemoteCapability();
        var registry = RemoteRegistry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => RemotePlan(request, registry, capability.Object));
        using var activity = new PlaybackDeliveryActivityStore();
        var gateway = new ProtocolProviderGateway(router.Object, registry, new ThrowingHttpClientFactory(),
            playbackActivity: activity, identities: identities, keptFiles: Service(), audioVerifier: FixtureAudioVerifier.Instance);
        var original = PlaybackContext(ProtocolKind.Jellyfin, owner, "ext-deezer-song-source-track");
        var context = new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId,
            original.VerifiedBackendPrincipalId, original.Principal, original.CorrelationId,
            original.Deadline, default)
        { RequestedItemId = original.RequestedItemId };
        foreach (var range in new string?[] { null, "bytes=5-" })
        {
            var stream = await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, range);
            Assert.NotNull(stream);
            Assert.Equal("deezer", stream.ServingProviderId);
            Assert.Null(stream.ServingManagedFileId);
            Assert.Equal(account, stream.ServingAccountId);
            stream.Response.Dispose();
        }
        capability.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task KeptPlaybackPreservesValidMimeParameters()
    {
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.Set<allstarr.Core.Downloads.ProviderDownloadArtifactEntity>().SingleAsync()).MimeType =
                "audio/flac; codecs=flac";
            await db.SaveChangesAsync();
        }
        await Service().PublishAsync(Request());
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var stream = await KeptGateway(router.Object).OpenStreamAsync(
            PlaybackContext(ProtocolKind.Jellyfin, other, "ext-deezer-song-source-track"),
            "allstarr", recordingId.ToString("D"), ProviderAudioQuality.Any, null);
        Assert.NotNull(stream);
        using var response = stream.Response;
        Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
        var parameter = Assert.Single(response.Content.Headers.ContentType.Parameters);
        Assert.Equal("codecs", parameter.Name);
        Assert.Equal("flac", parameter.Value);
        Assert.Equal(audio, await response.Content.ReadAsByteArrayAsync());
    }

    private ProtocolProviderGateway KeptGateway(
        IProviderRouter router,
        IApplicationCache? cache = null,
        PlaybackDeliveryActivityStore? playbackActivity = null,
        IEffectiveProviderPolicyResolver? effectivePolicies = null) =>
        new(router, new ProviderRegistry([]), new ThrowingHttpClientFactory(),
            applicationCache: cache, playbackActivity: playbackActivity, effectivePolicies: effectivePolicies,
            identities: identities, keptFiles: Service());

    private static Mock<IEffectiveProviderPolicyResolver> PlaybackPolicy(
        string providerId, ProviderUseMode mode)
    {
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty,
            AudioQualityPolicy.DefaultStep,
            0.07)
        {
            ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add(providerId, mode)
        };
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);
        return policies;
    }

    private static ProviderRoutePlan<IProviderStreamingCapability> EmptyStreamingPlan(
        ProviderRouteRequest request) => new(request, [],
        new(request.CorrelationId, request.Capability, null, null, []));

    private ProtocolExecutionContext PlaybackContext(ProtocolKind protocol, Guid userId, string itemId)
    {
        var backend = protocol == ProtocolKind.Subsonic ? "subsonic" : "jellyfin";
        return new(protocol, "fixture", userId.ToString("N"),
            new AllstarrPrincipal(userId, backend, "fixture", userId.ToString("N"), "Listener", false),
            "kept-playback", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None,
            new ProtocolClientDescriptor("fixture-client", "fixture-device"))
        { RequestedItemId = itemId };
    }

    private async Task DisableSourceAccountAsync()
    {
        await using var db = new AllstarrDbContext(database.Options);
        var source = await db.ProviderAccounts.SingleAsync(item => item.Id == account);
        source.Enabled = false;
        source.Revision++;
        await db.SaveChangesAsync();
    }

    private async Task SetListenerBackendAsync(ProtocolKind protocol)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var listener = await db.Users.SingleAsync(item => item.Id == other);
        listener.BackendType = protocol == ProtocolKind.Subsonic ? "subsonic" : "jellyfin";
        listener.BackendPrincipalId = other.ToString("N");
        await db.SaveChangesAsync();
    }

    private static async Task<PlaybackResponse> ExecuteResponseAsync(
        HttpResponseMessage response, string method, string? range)
    {
        using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        if (range != null) context.Request.Headers.Range = range;
        context.Response.Body = new MemoryStream();
        var result = await new ProtocolStreamingResponseAdapter().CreateAsync(
            context, response, CancellationToken.None, enableRangeProcessing: true);
        await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        return new(
            context.Response.StatusCode,
            ((MemoryStream)context.Response.Body).ToArray(),
            context.Response.ContentType,
            context.Response.ContentLength,
            context.Response.Headers.ContentRange.ToString());
    }

    private sealed record PlaybackResponse(
        int StatusCode, byte[] Body, string? ContentType, long? ContentLength, string? ContentRange);

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Kept playback must not create an HTTP client.");
    }

    private Mock<IProviderStreamingCapability> RemoteCapability()
    {
        var capability = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        capability.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
                "remote-lease", new Uri("https://media.example.test/deezer"), DateTimeOffset.UtcNow.AddMinutes(1),
                true, true, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry,
                (request, _) => Task.FromResult(RemoteResponse(request)))));
        return capability;
    }

    private static HttpResponseMessage RemoteResponse(HttpRequestMessage request)
    {
        if (request.Headers.Range?.Ranges.SingleOrDefault() is not { } range)
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent("remote bytes"u8.ToArray()) };
        var from = range.From ?? 0;
        var to = range.To ?? 11;
        var length = Math.Max(0, to - from + 1);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(new byte[checked((int)length)])
        };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, 12);
        return response;
    }

    private static ProviderRegistry RemoteRegistry(IProviderStreamingCapability capability) => new([
        new ProviderRegistration(
            new ProviderDescriptor(
                "deezer", "Deezer", "Fixture provider", ProviderOrigin.BuiltIn, "1", "1",
                [new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Streaming,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    "1", ["getStreamLease"], [ProviderAccountScope.Personal])],
                new ProviderPermissionDescriptor()),
            [capability])
    ]);

    private ProviderRoutePlan<IProviderStreamingCapability> RemotePlan(
        ProviderRouteRequest request, IProviderRegistry registry, IProviderStreamingCapability capability)
    {
        var provider = registry.GetRequired("deezer");
        var descriptor = provider.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Streaming);
        var candidate = new ProviderRouteCandidate<IProviderStreamingCapability>(
            0, provider, descriptor, capability,
            new ProviderExecutionContext(
                request.Actor, "deezer", new(account, "deezer", ProviderAccountScope.Personal, 7, ownerUserId: owner),
                request.Policy, request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken),
            new("deezer", ProviderResourceKind.Track, "source-track"));
        return new(request, [candidate], new(
            request.CorrelationId, ProviderCapabilityKind.Streaming, "deezer", account,
            [new("deezer", account, ProviderRouteDecisionStatus.Accepted, "selected", 0)]));
    }
}
