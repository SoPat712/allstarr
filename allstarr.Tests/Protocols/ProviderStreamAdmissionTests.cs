using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderStreamAdmissionTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task BusyRequestsDoNotOpenASourceAndAbortOrReaderDisposalReleasesExactlyOnce(ProtocolKind kind)
    {
        var limits = new MediaCapacityLimits(new ConfigurationManager { [MediaCapacityLimits.StreamsKey] = "1" });
        var admission = new ProviderStreamAdmission(limits);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var opens = 0;
        providers.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "fixture", ProviderAudioQuality.Any, null, false))
            .Returns(() =>
            {
                opens++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[32]) };
                return Task.FromResult<ProtocolProviderStream?>(new(response, new("fixture", new Uri("https://media.example.test/audio"),
                    DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/mpeg", "mp3", "mp3"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "fixture"));
            });
        var principal = new AllstarrPrincipal(Guid.NewGuid(), kind.ToString().ToLowerInvariant(), "primary", "listener", "Listener", false);
        var execution = new ProtocolExecutionContext(kind, "primary", "listener", principal, "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var opener = new ProviderStreamOpener(providers.Object, new(), admission: admission);
        using var aborted = new CancellationTokenSource();
        var one = new DefaultHttpContext { RequestAborted = aborted.Token };
        one.Request.Method = "GET";
        var result = Assert.IsType<FileStreamResult>(await opener.OpenAsync(one, execution, "deezer", "fixture", ProviderAudioQuality.Any));
        var busy = new DefaultHttpContext();
        busy.Request.Method = "GET";
        var failure = await Assert.ThrowsAsync<LocalCapacityException>(() => opener.OpenAsync(busy, execution with { IsOriginalDownload = true },
            "deezer", "fixture", ProviderAudioQuality.Any));
        Assert.Equal(503, allstarr.Controllers.JellyfinController.MapExternalStreamException(failure).statusCode);
        Assert.Equal(503, allstarr.Filters.SubsonicExceptionFilter.Map(failure).StatusCode);
        Assert.Equal("5", busy.Response.Headers.RetryAfter);
        Assert.Equal(1, opens);
        aborted.Cancel();
        var two = new DefaultHttpContext();
        two.Request.Method = "GET";
        var next = Assert.IsType<FileStreamResult>(await opener.OpenAsync(two, execution, "deezer", "fixture", ProviderAudioQuality.Any));
        result.FileStream.Dispose();
        await Assert.ThrowsAsync<LocalCapacityException>(() => opener.OpenAsync(new DefaultHttpContext(), execution,
            "deezer", "fixture", ProviderAudioQuality.Any));
        Assert.Equal(2, opens);
        next.FileStream.Dispose();
    }
}
