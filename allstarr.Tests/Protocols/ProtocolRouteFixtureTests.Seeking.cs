using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin", "/Items/ext-deezer-song-42/Download?apiKey=fixture-key", false)]
    [InlineData("Jellyfin", "/Items/ext-deezer-song-42/File?apiKey=fixture-key", false)]
    [InlineData("Jellyfin", "/Audio/ext-deezer-song-42/stream?apiKey=fixture-key", true)]
    [InlineData("Jellyfin", "/Audio/ext-deezer-song-42/stream.flac?apiKey=fixture-key", true)]
    [InlineData("Jellyfin", "/Audio/ext-deezer-song-42/universal?apiKey=fixture-key&Container=flac&AudioCodec=flac", true)]
    [InlineData("Subsonic", "/rest/stream.view?id=ext-deezer-song-42&u=fixture&p=fixture&v=1.13.0&c=Feishin", true)]
    public async Task ExternalSeekRoutesKeepTheSelectedPartialBodyAndItsExactLength(string backend, string path, bool cached)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent([8, 9, 10, 11]) };
        response.Content.Headers.ContentType = new("audio/flac");
        response.Content.Headers.ContentRange = new(8, 11, 32);
        var lease = new ProviderStreamLease("seek", new Uri("https://media.example.test/seek"),
            DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42",
                It.IsAny<ProviderAudioQuality>(), "bytes=8-11", false))
            .ReturnsAsync(new ProtocolProviderStream(response, lease, "deezer", "42", IsCached: cached));
        using var factory = DownloadFactory(backend, gateway.Object);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new(8, 11);
        using var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, result.StatusCode);
        Assert.Equal("bytes 8-11/32", result.Content.Headers.ContentRange!.ToString());
        Assert.Equal(4, result.Content.Headers.ContentLength);
        Assert.Equal(new byte[] { 8, 9, 10, 11 }, await result.Content.ReadAsByteArrayAsync());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }
}
