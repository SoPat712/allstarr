using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderCtsDiagnosticRunnerTests
{
    [Theory]
    [InlineData("bytes 0-9/100", 10, "byte-range")]
    [InlineData("bytes 0-9/*", 10, "sequential")]
    [InlineData("bytes 0-9/100", 9, "sequential")]
    [InlineData("items 0-9/100", 10, "sequential")]
    [InlineData("bytes */100", 10, "sequential")]
    [InlineData("invalid", 10, "sequential")]
    public void SeekRung_RequiresActualBytesAndKnownTotal(string contentRange, long length, string expected)
    {
        var result = new ProviderCtsDiagnosticResult
        {
            Succeeded = true,
            LeaseSupportsByteRanges = true,
            LeaseSupportsSeeking = false,
            UpstreamStatusCode = 206,
            ContentRange = contentRange,
            ContentLength = length
        };
        Assert.Equal(expected, result.SeekRung);
    }

    [Fact]
    public async Task Measure_BoundsAndRedactsTheRecordedMediaSample()
    {
        const int sampleLimit = 65_536;
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
            },
            qualityDowngradeReason: "The account tier limits this track to lossless audio.");
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
            ProviderAccountScope.Personal,
            revision: 1,
            ownerUserId: userId);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        accounts.Setup(item => item.ResolveAsync(
                It.Is<ProviderRouteAccountRequest>(request => request.RequestedAccountId == accountId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderRouteAccountResolution(account, 1));
        var health = new Mock<IProviderOutcomeObserver>(MockBehavior.Strict);
        health.Setup(item => item.Observe(
            It.Is<ProviderExecutionContext>(context => context.ProviderId == "qobuz" && context.Account != null &&
                context.Account.AccountId == accountId && context.Account.Revision == 1),
            ProviderCapabilityKind.Streaming, null, It.IsAny<TimeSpan>()));
        using var selector = new ProviderCtsTrackSelector(
            Mock.Of<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict));
        var runner = new ProviderCtsDiagnosticRunner(
            providers.Object,
            accounts.Object,
            selector,
            health.Object);
        var actor = new ProviderActorContext(
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
        Assert.Equal(
            "The account tier limits this track to lossless audio.",
            result.QualityDowngradeReason);
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
        var observer = new Mock<IProviderOutcomeObserver>(MockBehavior.Strict);
        observer.Setup(item => item.Observe(It.Is<ProviderExecutionContext>(context => context.Account == null),
            ProviderCapabilityKind.Streaming, null, It.IsAny<TimeSpan>()));
        var runner = new ProviderCtsDiagnosticRunner(
            providers.Object,
            Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict),
            selector,
            observer.Object);
        var actor = new ProviderActorContext(
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
        observer.VerifyAll();
        capability.VerifyAll();
        providers.VerifyAll();
    }

    [Fact]
    public async Task Measure_RequiresAnAccountForAccountBoundStreaming()
    {
        IProviderStreamingCapability? registered = Mock.Of<IProviderStreamingCapability>();
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        providers.Setup(item => item.TryGetCapability<IProviderStreamingCapability>(
                "qobuz", ProviderCapabilityKind.Streaming, out registered))
            .Returns(true);
        providers.Setup(item => item.GetRequired("qobuz"))
            .Returns(Descriptor("qobuz", ProviderAccountRequirement.Required));
        using var selector = new ProviderCtsTrackSelector(
            Mock.Of<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict));
        var runner = new ProviderCtsDiagnosticRunner(
            providers.Object,
            Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict),
            selector,
            Mock.Of<IProviderOutcomeObserver>(MockBehavior.Strict));
        var actor = new ProviderActorContext(
            ProviderActorKind.User,
            Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "principal"));

        var result = await runner.MeasureAsync(
            actor,
            "qobuz",
            null,
            ProviderAudioQuality.Any,
            "missing-account");

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("account-resolution", result.Stage);
        providers.VerifyAll();
    }

    [Fact]
    public async Task Measure_SearchesTheProviderWhenNoKnownTrackIsStored()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var lease = new ProviderStreamLease(
            "lease",
            new Uri("https://media.example.test/searched"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            supportsByteRanges: false,
            supportsSeeking: false,
            new ProviderMediaFormat("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            }));
        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        streaming.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "searched-track")))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(lease));
        var queries = new List<string>();
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(item => item.SearchTracksAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderMetadataSearchRequest request) =>
            {
                queries.Add(request.Query);
                return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("amazon", queries.Count == 1
                    ? []
                    : [new ProviderTrackMetadata(new("amazon", ProviderResourceKind.Track, "searched-track"),
                        "Song", [new("Artist")])]));
            });
        var providers = AccountFreeRegistry("amazon", streaming.Object, metadata.Object);
        var observer = new Mock<IProviderOutcomeObserver>(MockBehavior.Strict);
        observer.Setup(item => item.Observe(It.IsAny<ProviderExecutionContext>(),
            ProviderCapabilityKind.Streaming, null, It.IsAny<TimeSpan>()));
        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var runner = new ProviderCtsDiagnosticRunner(
            providers.Object,
            Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict),
            selector,
            observer.Object);

        var result = await runner.MeasureAsync(
            Actor(), "amazon", null, ProviderAudioQuality.Lossless, "search-fallback");

        Assert.True(result.Succeeded);
        Assert.Equal("search", result.SelectionMode);
        Assert.Equal(2, queries.Count);
        streaming.VerifyAll();
    }

    [Fact]
    public async Task Measure_ExplainsWhenTheProviderHasNoSongToTest()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(item => item.SearchTracksAsync(
                It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("amazon", [])));
        var providers = AccountFreeRegistry(
            "amazon", Mock.Of<IProviderStreamingCapability>(MockBehavior.Strict), metadata.Object);
        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var runner = new ProviderCtsDiagnosticRunner(
            providers.Object,
            Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict),
            selector,
            Mock.Of<IProviderOutcomeObserver>(MockBehavior.Strict));

        var result = await runner.MeasureAsync(
            Actor(), "amazon", null, ProviderAudioQuality.Lossless, "search-empty");

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal("track-selection", result.Stage);
        Assert.Equal(
            "amazon has no song to test with. None of its songs appear in your playlists yet, and its search returned no results.",
            result.Error);
        metadata.Verify(item => item.SearchTracksAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Exactly(3));
    }

    private static ProviderActorContext Actor() => new(
        ProviderActorKind.User,
        Guid.CreateVersion7(),
        new ProviderBackendPrincipal("jellyfin", "fixture", "principal"));

    private static Mock<IProviderRegistry> AccountFreeRegistry(
        string providerId,
        IProviderStreamingCapability streaming,
        IProviderMetadataCapability metadata)
    {
        IProviderStreamingCapability? registeredStreaming = streaming;
        IProviderMetadataCapability? registeredMetadata = metadata;
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        providers.Setup(item => item.TryGetCapability<IProviderStreamingCapability>(
                providerId, ProviderCapabilityKind.Streaming, out registeredStreaming))
            .Returns(true);
        providers.Setup(item => item.TryGetCapability<IProviderMetadataCapability>(
                providerId, ProviderCapabilityKind.Metadata, out registeredMetadata))
            .Returns(true);
        providers.Setup(item => item.GetRequired(providerId))
            .Returns(Descriptor(providerId, ProviderAccountRequirement.None));
        return providers;
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
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
            requirement == ProviderAccountRequirement.None ? [] : [ProviderAccountScope.Personal])],
        new ProviderPermissionDescriptor());
}
