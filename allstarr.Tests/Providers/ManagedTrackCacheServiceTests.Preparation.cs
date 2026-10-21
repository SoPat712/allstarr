using Microsoft.Extensions.Configuration;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    [Fact]
    public async Task PreparationDeclinesResponsesLargerThanTheCacheBudget()
    {
        var root = CreateRoot();
        try
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Library:DownloadPath"] = root, ["Cache:MediaMaximumBytes"] = "8" }).Build();
            await using var cache = new allstarr.Services.Common.ManagedTrackCacheService(configuration,
                Microsoft.Extensions.Options.Options.Create(new allstarr.Models.Settings.SubsonicSettings
                { StorageMode = allstarr.Models.Settings.StorageMode.Cache }), Mock.Of<ILocalLibraryService>(MockBehavior.Strict),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Services.Common.ManagedTrackCacheService>.Instance);
            using var response = Response(System.Net.HttpStatusCode.OK, new byte[16]);
            var original = response.Content;
            await cache.WrapAsync(ProviderStream(response), Context() with { IsPreparation = true }, "deezer", "track-1", false,
                () => Task.FromResult<Song?>(null), CancellationToken.None);
            Assert.Same(original, response.Content);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueCancellationStopsOnlyTransfersNotClaimedByRealPlayback(bool realPlayback)
    {
        await using var fixture = new ProgressiveFixture(knownLength: true, provider: "apple-musickit");
        using var cancel = new CancellationTokenSource();
        var context = Context() with { IsPreparation = true };
        await fixture.Service.WrapAsync(ProviderStream(fixture.Response) with
        {
            ServingProviderId = "apple-musickit",
            ServingExternalId = "track-1",
            ServingAccountId = fixture.Scope.ProviderAccountId,
            ServingAccountRevision = 4
        }, context, "apple-musickit", "track-1", false,
            () => Task.FromResult<Song?>(null), cancel.Token);
        var preparedReader = await fixture.Response.Content.ReadAsStreamAsync();
        Assert.Equal(1, await preparedReader.ReadAsync(new byte[1]));
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var playing = realPlayback ? (await fixture.OpenAsync())!.Response : null;
        cancel.Cancel();
        if (playing != null)
        {
            using (playing)
            {
                fixture.Source.Release.TrySetResult();
                Assert.Equal(fixture.Bytes, await playing.Content.ReadAsByteArrayAsync());
                Assert.NotNull(fixture.PublishedPath);
            }
        }
        else
        {
            await fixture.Source.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(fixture.PublishedPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationHandoffRetainsCompletedFileFactsAfterQueueCancellation(bool knownLength)
    {
        await using var fixture = new ProgressiveFixture(knownLength, provider: "apple-musickit");
        using var cancel = new CancellationTokenSource();
        var prepared = ProviderStream(fixture.Response) with
        {
            ServingProviderId = "apple-musickit",
            ServingExternalId = "track-1",
            ServingAccountId = fixture.Scope.ProviderAccountId,
            ServingAccountRevision = 4
        };
        await fixture.Service.WrapAsync(prepared, Context() with { IsPreparation = true },
            "apple-musickit", "track-1", false, () => Task.FromResult<Song?>(null), cancel.Token);
        var preparing = await fixture.Response.Content.ReadAsStreamAsync();
        Assert.Equal(1, await preparing.ReadAsync(new byte[1]));
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var playing = await fixture.OpenAsync();
        Assert.NotNull(playing);
        using var response = playing.Response;
        using var activity = new allstarr.Services.Common.PlaybackDeliveryActivityStore();
        var context = Context();
        const string itemId = "ext-apple-musickit-song-track-1";
        activity.StreamOpened(context, itemId, playing.EffectiveQuality, playing);
        cancel.Cancel();
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(fixture.PublishedPath);
        var observed = activity.StreamFor(context, itemId, playing.EffectiveQuality);
        Assert.NotNull(observed);
        Assert.Equal(fixture.Bytes.Length, observed.Size);
        Assert.Equal("flac", observed.Media!.Codec);
        Assert.True(activity.WasStreamOpened(itemId));
    }

    [Fact]
    public async Task PreparedSongIsASeekableCacheHitOnItsFirstRealOpen()
    {
        var root = CreateRoot();
        string? published = null;
        try
        {
            var library = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            library.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "apple-musickit", "next"))
                .ReturnsAsync(() => published);
            library.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((_, _, path) => published = path).Returns(Task.CompletedTask);
            await using var cache = CreateService(root, "Cache", library.Object);
            var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
            var preparation = Context() with { IsPreparation = true, RequestedItemId = "ext-apple-download-song-next" };
            using var response = Response(System.Net.HttpStatusCode.OK, bytes);
            var routed = ProviderStream(response) with { ServingProviderId = "apple-musickit", ServingExternalId = "next" };
            var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
            gateway.Setup(item => item.OpenStreamAsync(preparation, "apple-download", "next", ProviderAudioQuality.Any, null, false))
                .ReturnsAsync(routed);
            gateway.Setup(item => item.GetSongAsync(preparation, "apple-musickit", "next")).ReturnsAsync(new Song { Title = "Fixture", Artist = "Fixture" });
            var opener = new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter(), cache);
            await opener.PrepareAsync(preparation, "ext-apple-download-song-next");
            Assert.NotNull(published);
            var real = Context();
            gateway.Setup(item => item.OpenStreamAsync(real, "apple-download", "next", ProviderAudioQuality.Any, "bytes=10-19", false))
                .Returns(() => cache.TryOpenAsync(new("apple-musickit", ProviderResourceKind.Track, "next"),
                    new DownloadedSongMappingScope(routed.ServingAccountId, routed.EffectiveQuality), CancellationToken.None));
            var http = new DefaultHttpContext();
            http.Request.Headers.Range = "bytes=10-19";
            var result = Assert.IsType<FileStreamResult>(await opener.OpenAsync(http, real, "apple-download", "next", ProviderAudioQuality.Any));
            Assert.True(result.EnableRangeProcessing);
            Assert.True(result.FileStream.CanSeek);
            result.FileStream.Position = 10;
            var range = new byte[10];
            Assert.Equal(10, await result.FileStream.ReadAsync(range));
            Assert.Equal(bytes[10..20], range);
            await result.FileStream.DisposeAsync();
            gateway.VerifyAll();
        }
        finally { DeleteRoot(root); }
    }
}
