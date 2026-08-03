using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderCtsDiagnosticRunnerTests
{
    [Fact]
    public async Task Measure_BoundsAndRedactsTheRecordedMediaSample()
    {
        const int sampleLimit = 65_536;
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var body = Enumerable.Range(0, 80_000).Select(value => (byte)(value % 251)).ToArray();
        string? observedRange = null;
        var media = new ProviderMediaFormat("audio/flac", "flac", "flac", 1_411_000, 44_100, 16, 2);
        var lease = new ProviderStreamLease(
            "lease",
            new Uri("https://media.example.test/track?signed=private-secret"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            supportsByteRanges: true,
            supportsSeeking: true,
            media,
            ProviderStreamRetryBehavior.DoNotRetry,
            (request, _) =>
            {
                observedRange = request.Headers.Range?.ToString();
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(body)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, body.Length - 1, body.Length);
                response.Headers.AcceptRanges.Add("bytes");
                response.Headers.TryAddWithoutValidation("X-Cache", "HIT private-cache-node");
                return Task.FromResult(response);
            });
        var capability = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        capability.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderStreamLeaseRequest>(request =>
                    request.TrackId.Value == "private-track-id" &&
                    request.RequestedQuality == ProviderAudioQuality.Lossless)))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(lease));
        IProviderStreamingCapability? registered = capability.Object;
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        providers.Setup(item => item.TryGetCapability<IProviderStreamingCapability>(
                "qobuz", ProviderCapabilityKind.Streaming, out registered))
            .Returns(true);
        providers.Setup(item => item.GetRequired("qobuz"))
            .Returns(Descriptor("qobuz", ProviderAccountRequirement.Required));
        var account = new ProviderAccountContext(
            accountId,
            "qobuz",
            ProviderAccountScope.User,
            revision: 1,
            tenantId: tenantId,
            ownerUserId: userId);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(
                It.Is<ProviderRouteAccountRequest>(request => request.RequestedAccountId == accountId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderRouteAccountResolution(account, 1));
        var health = new Mock<IDurableProviderHealthObservationStore>(MockBehavior.Strict);
        health.Setup(item => item.RecordAsync(
                "qobuz",
                accountId.ToString("N"),
                "click-to-stream",
                allstarr.Core.Storage.ProviderHealthState.Healthy,
                It.IsAny<long?>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DurableProviderHealthSnapshot?)null);
        using var selector = new ProviderCtsTrackSelector(
            Mock.Of<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict));
        var runner = new ProviderCtsDiagnosticRunner(
            providers.Object,
            accounts.Object,
            selector,
            health.Object);
        var actor = new ProviderActorContext(
            tenantId,
            ProviderActorKind.User,
            userId,
            new ProviderBackendPrincipal("jellyfin", "fixture", "principal"));

        var result = await runner.MeasureAsync(
            actor,
            "qobuz",
            accountId,
            ProviderAudioQuality.Lossless,
            "fixture-correlation",
            "private-track-id");

        Assert.True(result.Succeeded);
        Assert.Equal("bytes=0-65535", observedRange);
        Assert.Equal(sampleLimit, result.SampleBytes);
        Assert.Equal(sampleLimit, result.Limit.SampleBytes);
        Assert.Equal(206, result.UpstreamStatusCode);
        Assert.Equal(body.Length, result.ContentLength);
        Assert.Equal($"bytes 0-{body.Length - 1}/{body.Length}", result.ContentRange);
        Assert.True(result.AcceptsByteRanges);
        Assert.True(result.LeaseSupportsByteRanges);
        Assert.True(result.LeaseSupportsSeeking);
        Assert.Equal(media, result.Media);
        Assert.Equal("hit", result.CacheState);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(body.AsSpan(0, sampleLimit))).ToLowerInvariant(),
            result.SampleSha256);
        Assert.NotNull(result.RouteMilliseconds);
        Assert.NotNull(result.PreparationMilliseconds);
        Assert.NotNull(result.UpstreamHeadersMilliseconds);
        Assert.NotNull(result.FirstByteMilliseconds);
        Assert.NotNull(result.TotalMilliseconds);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private-track-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-cache-node", json, StringComparison.Ordinal);
        capability.VerifyAll();
        providers.VerifyAll();
        accounts.VerifyAll();
        health.VerifyAll();
    }

    [Fact]
    public async Task Measure_AllowsAccountFreeStreamingWithoutInventingAnAccount()
    {
        var media = new ProviderMediaFormat("audio/flac", "flac", "flac");
        var lease = new ProviderStreamLease(
            "lease",
            new Uri("https://media.example.test/apple"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            supportsByteRanges: false,
            supportsSeeking: false,
            media,
            ProviderStreamRetryBehavior.DoNotRetry,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            }));
        var capability = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        capability.Setup(item => item.GetStreamLeaseAsync(
                It.Is<ProviderExecutionContext>(context => context.Account == null),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "apple-track")))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(lease));
        IProviderStreamingCapability? registered = capability.Object;
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        providers.Setup(item => item.TryGetCapability<IProviderStreamingCapability>(
                "apple-download", ProviderCapabilityKind.Streaming, out registered))
            .Returns(true);
        providers.Setup(item => item.GetRequired("apple-download"))
            .Returns(Descriptor("apple-download", ProviderAccountRequirement.None));
        using var selector = new ProviderCtsTrackSelector(
            Mock.Of<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict));
        var runner = new ProviderCtsDiagnosticRunner(
            providers.Object,
            Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict),
            selector,
            Mock.Of<IDurableProviderHealthObservationStore>(MockBehavior.Strict));
        var actor = new ProviderActorContext(
            Guid.CreateVersion7(),
            ProviderActorKind.User,
            Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "principal"));

        var result = await runner.MeasureAsync(
            actor,
            "apple-download",
            null,
            ProviderAudioQuality.Lossless,
            "account-free-correlation",
            "apple-track");

        Assert.True(result.Succeeded);
        Assert.Null(result.ProviderAccountId);
        Assert.Equal(4, result.SampleBytes);
        Assert.Equal(media, result.Media);
        capability.VerifyAll();
        providers.VerifyAll();
    }

    private static ProviderDescriptor Descriptor(
        string providerId,
        ProviderAccountRequirement requirement) => new(
        providerId,
        providerId,
        "Fixture streaming provider.",
        ProviderOrigin.BuiltIn,
        "1",
        "1",
        [new ProviderCapabilityDescriptor(
            ProviderCapabilityKind.Streaming,
            ProviderCapabilitySupportState.Supported,
            requirement,
            "1",
            ["getStreamLease"],
            requirement == ProviderAccountRequirement.None ? [] : [ProviderAccountScope.User])],
        new ProviderPermissionDescriptor());
}
