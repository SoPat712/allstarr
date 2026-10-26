using System.Collections.Immutable;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin", "Download", false, false)]
    [InlineData("Jellyfin", "Download", true, false)]
    [InlineData("Jellyfin", "File", false, false)]
    [InlineData("Subsonic", "download", false, false)]
    [InlineData("Subsonic", "download", true, false)]
    [InlineData("Jellyfin", "Download", false, true)]
    [InlineData("Jellyfin", "Download", true, true)]
    [InlineData("Subsonic", "download", false, true)]
    [InlineData("Subsonic", "download", true, true)]
    public async Task OriginalDownload_SequentialQueryRequestsReattachAfterAbortAndServeIndependentFarRanges(
        string backend, string endpoint, bool canonical, bool completedBeforeAbort)
    {
        await using var fixture = new OriginalRangeFixture(backend, endpoint, canonical, completedBeforeAbort);
        using var firstCancellation = new CancellationTokenSource();
        using var initialRequest = new HttpRequestMessage(HttpMethod.Get, fixture.Url);
        initialRequest.Headers.Range = new(0, 65_535);
        using var initial = await fixture.Client.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, firstCancellation.Token);
        Assert.True(initial.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent);
        Assert.Equal("audio/mpeg", initial.Content.Headers.ContentType?.MediaType);
        await using var initialBody = await initial.Content.ReadAsStreamAsync();
        var prefix = new byte[4096];
        await initialBody.ReadExactlyAsync(prefix);
        Assert.Equal(fixture.Bytes[..4096], prefix);
        if (completedBeforeAbort) await fixture.Source.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        else await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        firstCancellation.Cancel();
        initial.Dispose();
        if (!completedBeforeAbort) Assert.False(fixture.Source.Closed.Task.IsCompleted);

        const int start = 400_000;
        using var oneRequest = new HttpRequestMessage(HttpMethod.Get, fixture.Url);
        using var twoRequest = new HttpRequestMessage(HttpMethod.Get, fixture.Url);
        oneRequest.Headers.Range = new(start, start + 8191);
        twoRequest.Headers.Range = new(start + 16_384, start + 24_575);
        var oneOpening = fixture.Client.SendAsync(oneRequest, HttpCompletionOption.ResponseHeadersRead);
        var twoOpening = fixture.Client.SendAsync(twoRequest, HttpCompletionOption.ResponseHeadersRead);
        await fixture.ThreeRoutes.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Source.Release.TrySetResult();
        using var one = await oneOpening.WaitAsync(TimeSpan.FromSeconds(8));
        using var two = await twoOpening.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(HttpStatusCode.PartialContent, one.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, two.StatusCode);
        Assert.Equal($"bytes {start}-{start + 8191}/{fixture.Bytes.Length}", one.Content.Headers.ContentRange?.ToString());
        Assert.Equal(8192, one.Content.Headers.ContentLength);
        Assert.Equal($"bytes {start + 16_384}-{start + 24_575}/{fixture.Bytes.Length}", two.Content.Headers.ContentRange?.ToString());
        Assert.Equal(8192, two.Content.Headers.ContentLength);
        Assert.Equal(fixture.Bytes[start..(start + 8192)], await one.Content.ReadAsByteArrayAsync());
        Assert.Equal(fixture.Bytes[(start + 16_384)..(start + 24_576)], await two.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, fixture.Fetches);
        Assert.False(fixture.Activity.WasStreamOpened(fixture.ItemId));
        Assert.Empty(Directory.Exists(fixture.Root) ? Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories) : []);
        fixture.Local.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Jellyfin", "Download", false)]
    [InlineData("Jellyfin", "Download", true)]
    [InlineData("Subsonic", "download", false)]
    public async Task OriginalDownload_ExistingRangedSourcesKeepExactTransport(string backend, string endpoint, bool canonical)
    {
        await using var fixture = new OriginalRangeFixture(backend, endpoint, canonical, true, supportsRanges: true);
        using var initial = await fixture.Client.GetAsync(fixture.Url);
        Assert.Equal(fixture.Bytes, await initial.Content.ReadAsByteArrayAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Url);
        request.Headers.Range = new(400_000, 408_191);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 400000-408191/524288", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(fixture.Bytes[400_000..408_192], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, fixture.Fetches);
        fixture.Local.VerifyNoOtherCalls();
    }

    private sealed class OriginalRangeFixture : IAsyncDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        public readonly byte[] Bytes = Enumerable.Range(0, 524_288).Select(value => (byte)(value % 251)).ToArray();
        public readonly OriginalRangeSource Source;
        public readonly Mock<ILocalLibraryService> Local = new(MockBehavior.Strict);
        public readonly PlaybackDeliveryActivityStore Activity = new();
        public readonly TaskCompletionSource ThreeRoutes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly HttpClient Client;
        public readonly string ItemId;
        public readonly string Url;
        private readonly ManagedTrackCacheService cache;
        private readonly ProtocolFactory factory;
        private int routes;
        public int Fetches;

        public OriginalRangeFixture(string backend, string endpoint, bool canonical, bool complete, bool supportsRanges = false)
        {
            Source = new(Bytes);
            if (complete) Source.Release.TrySetResult();
            ItemId = canonical ? "ext-allstarr-song-76d17d96-70e2-46b1-a3bf-358a6eb67e2b" : "ext-deezer-song-42";
            Url = backend == "Jellyfin" ? $"/Items/{ItemId}/{endpoint}?api_key=fixture-key"
                : $"/rest/{endpoint}?id={ItemId}&u=fixture&p=fixture&v=1.16.1&c=Feishin&f=json";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Library:DownloadPath"] = Root }).Build();
            cache = new(configuration, Options.Create(new SubsonicSettings
            { StorageMode = backend == "Jellyfin" ? StorageMode.Permanent : StorageMode.Cache }),
                Local.Object, NullLogger<ManagedTrackCacheService>.Instance);
            var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
            streaming.SetupGet(item => item.ProviderId).Returns("deezer");
            streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
            streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
                .Returns((ProviderExecutionContext _, ProviderStreamLeaseRequest request) =>
                {
                    Assert.Equal(ProviderAudioQuality.Any, request.RequestedQuality);
                    return Task.FromResult(ProviderOutcome<ProviderStreamLease>.Success(new("original",
                        new Uri("https://media.example.test/original"), DateTimeOffset.UtcNow.AddMinutes(1),
                        supportsRanges, supportsRanges, new("audio/mpeg", "mp3", "mp3"), ProviderStreamRetryBehavior.DoNotRetry,
                        (request, _) =>
                        {
                            var fetch = Interlocked.Increment(ref Fetches);
                            var range = supportsRanges ? request.Headers.Range?.Ranges.Single() : null;
                            var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                            {
                                Content = range != null ? new ByteArrayContent(Bytes[(int)range.From!.Value..((int)(range.To ?? Bytes.Length - 1) + 1)])
                                    : new StreamContent(supportsRanges || fetch > 1 ? new MemoryStream(Bytes) : Source)
                            };
                            response.Content.Headers.ContentType = new("audio/mpeg");
                            response.Content.Headers.ContentLength = range == null ? Bytes.Length : range.To!.Value - range.From!.Value + 1;
                            if (range != null) response.Content.Headers.ContentRange = new(range.From!.Value, range.To!.Value, Bytes.Length);
                            return Task.FromResult(response);
                        })));
                });
            var descriptor = new ProviderDescriptor("deezer", "Deezer", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
                [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None, "1", ["getStreamLease"])], new());
            var registry = new ProviderRegistry([new(descriptor, [streaming.Object])]);
            var router = new Mock<IProviderRouter>(MockBehavior.Strict);
            router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) =>
                {
                    if (Interlocked.Increment(ref routes) == 3) ThreeRoutes.TrySetResult();
                    return new ProviderRoutePlan<IProviderStreamingCapability>(request,
                        [new(0, descriptor, descriptor.Capabilities.Single(), streaming.Object,
                            new(request.Actor, "deezer", null, request.Policy, request.OperationId, request.CorrelationId,
                                request.Deadline, request.CancellationToken), new("deezer", ProviderResourceKind.Track, "42"))],
                        new(request.CorrelationId, ProviderCapabilityKind.Streaming, "deezer", null, []));
                });
            var gateway = new ProtocolProviderGateway(router.Object, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
                configuration, managedTrackCache: cache, playbackActivity: Activity);
            factory = new(backend, _ => DownloadAuthentication(backend), services =>
            {
                services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
                var policies = new Mock<IEffectiveProviderPolicyResolver>();
                policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                        ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                        ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                    { UserId = user });
                services.RemoveAll<IEffectiveProviderPolicyResolver>();
                services.AddSingleton(policies.Object);
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton<IProtocolProviderGateway>(gateway);
                services.RemoveAll<ManagedTrackCacheService>();
                services.AddSingleton(cache);
            });
            Client = factory.CreateClient();
        }

        public async ValueTask DisposeAsync()
        {
            Source.Release.TrySetResult();
            Client.Dispose();
            factory.Dispose();
            await cache.DisposeAsync();
            Activity.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class OriginalRangeSource(byte[] bytes) : Stream
    {
        private int position;
        public readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position >= 4096)
            {
                Waiting.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            var count = Math.Min(buffer.Length, Math.Min(4096, bytes.Length - position));
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Closed.TrySetResult(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
