using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData("allstarr", 0)]
    [InlineData("deezer", 1)]
    public async Task CompleteMeasuredReplayDoesNotReadOrRewriteStoredMediaFacts(string provider, int identityWrites)
    {
        var root = Directory.CreateTempSubdirectory("allstarr-replay-").FullName;
        var path = Path.Combine(root, "original.wav");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(16036);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(16000);
            writer.Write(new byte[16000]);
        }
        await File.WriteAllBytesAsync(path, bytes.ToArray());
        try
        {
            var library = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            library.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", It.IsAny<string>()))
                .ReturnsAsync(path);
            await using var cache = new ManagedTrackCacheService(new ConfigurationBuilder().Build(),
                Options.Create(new SubsonicSettings()), library.Object, NullLogger<ManagedTrackCacheService>.Instance);
            var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
            var media = identities.As<ITrackMediaFactsStore>();
            identities.Setup(item => item.RememberSourceAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderExternalResourceId>(),
                It.IsAny<string>(), It.IsAny<allstarr.Core.Storage.ProviderIdentityScope>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            var first = Streaming("deezer", (_, _) => throw new InvalidOperationException("No upstream open is needed."));
            var second = Streaming("qobuz", (_, _) => throw new InvalidOperationException("No upstream open is needed."));
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, identities: identities.Object);
            using var response = (await gateway.OpenStreamAsync(ClientContext(), provider, "recording", ProviderAudioQuality.Any, "bytes=44-"))!.Response;
            Assert.Equal("audio/wav", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(bytes.ToArray(), await response.Content.ReadAsByteArrayAsync());
            media.Verify(item => item.ApplyStoredMediaFactsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<IReadOnlyList<allstarr.Models.Domain.Song>>(),
                It.IsAny<CancellationToken>()), Times.Never);
            media.Verify(item => item.RememberMediaFactsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderExternalResourceId>(),
                It.IsAny<TrackMediaFacts>(), It.IsAny<CancellationToken>()), Times.Never);
            identities.Verify(item => item.RememberSourceAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderExternalResourceId>(),
                It.IsAny<string>(), It.IsAny<allstarr.Core.Storage.ProviderIdentityScope>(), null, It.IsAny<CancellationToken>()), Times.Exactly(identityWrites));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "bytes=10-19")]
    [InlineData(ProtocolKind.Subsonic, "bytes=10-19")]
    [InlineData(ProtocolKind.Jellyfin, "bytes=0-")]
    [InlineData(ProtocolKind.Subsonic, "bytes=-10")]
    public async Task ReplayAfterRestartChecksEveryAuthorizedCompleteCacheBeforeOpeningAProvider(ProtocolKind dialect, string range)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "original.flac");
        var bytes = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var library = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            library.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "deezer-track"))
                .ReturnsAsync((string?)null);
            library.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "qobuz", "qobuz-track"))
                .ReturnsAsync(path);
            await using var cache = new ManagedTrackCacheService(new ConfigurationBuilder().Build(),
                Options.Create(new SubsonicSettings()), library.Object, NullLogger<ManagedTrackCacheService>.Instance);
            using var emptySelections = new PlaybackDeliveryActivityStore();
            var first = Streaming("deezer", (_, _) => throw new InvalidOperationException("Replay must use the complete original."));
            var second = Streaming("qobuz", (_, _) => throw new InvalidOperationException("Replay must use the complete original."));
            var (gateway, _) = FailoverGateway(first, second, emptySelections, managedTrackCache: cache);
            var original = ClientContext();
            var context = new ProtocolExecutionContext(dialect, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
                original.Principal! with { BackendType = dialect.ToString().ToLowerInvariant() }, original.CorrelationId, original.Deadline, default, original.Client);
            var opened = await gateway.OpenStreamAsync(context, "allstarr", Guid.NewGuid().ToString("D"), ProviderAudioQuality.Any, range);
            using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
            Assert.True(opened.IsCached);
            using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Method = HttpMethods.Get;
            http.Request.Headers.Range = range;
            http.Response.Body = new MemoryStream();
            var result = await new ProtocolStreamingResponseAdapter().CreateAsync(http, response, default, true, providerStream: true);
            await result.ExecuteResultAsync(new ActionContext(http, new(), new()));
            Assert.Equal(StatusCodes.Status206PartialContent, http.Response.StatusCode);
            var expected = range == "bytes=10-19" ? bytes[10..20] : range == "bytes=-10" ? bytes[^10..] : bytes;
            Assert.Equal(expected, ((MemoryStream)http.Response.Body).ToArray());
            foreach (var provider in new[] { first, second })
                provider.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task FirstRequestMayBeARangeAndAFailedPreviousSourceFallsBack(ProtocolKind dialect, bool previous)
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var first = Streaming("deezer", (request, _) => Task.FromResult(AudioResponse(
            request.Headers.Range == null ? HttpStatusCode.OK : HttpStatusCode.BadGateway)));
        var second = Streaming("qobuz", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([2, 3, 4]) };
            response.Content.Headers.ContentRange = new(1, 3, 4);
            return Task.FromResult(response);
        });
        var (gateway, _) = FailoverGateway(first, second, activity);
        var original = ClientContext();
        var context = new ProtocolExecutionContext(dialect, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
            original.Principal! with { BackendType = dialect.ToString().ToLowerInvariant() }, original.CorrelationId, original.Deadline, default, original.Client);
        if (previous)
        {
            using var started = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
            Assert.NotNull(activity.StreamFor(context, "ext-deezer-song-source-track", ProviderAudioQuality.Any));
        }
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, "bytes=1-"))!.Response;
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
    }
}
