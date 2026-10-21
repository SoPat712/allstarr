using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Matching;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string FeishinClientInfo =
        """{"name":"Feishin","platform":"fixture","directPlayProfiles":[{"containers":["flac"],"audioCodecs":["flac"],"protocols":["http"]}]}""";

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicTranscodeDecision_ProviderSongPlaysDirectlyWithoutAskingTheBackend(string format)
    {
        var observed = new List<ObservedRequest>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Lossless, null, true))
            .ReturnsAsync(() => new ProtocolProviderStream(new HttpResponseMessage(HttpStatusCode.OK),
                new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                    true, true, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42"));
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            observed.Add(Observe(request));
            return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/rest/getTranscodeDecision.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}&mediaId=ext-deezer-song-42&mediaType=song",
            new StringContent(FeishinClientInfo, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("ok", root.GetProperty("status").GetString());
            var decision = root.GetProperty("transcodeDecision");
            Assert.True(decision.GetProperty("canDirectPlay").GetBoolean());
            Assert.False(decision.GetProperty("canTranscode").GetBoolean());
        }
        else
        {
            var decision = XDocument.Parse(body).Descendants().Single(element => element.Name.LocalName == "transcodeDecision");
            Assert.Equal("true", decision.Attribute("canDirectPlay")?.Value);
            Assert.Equal("false", decision.Attribute("canTranscode")?.Value);
        }
        Assert.All(observed, request => Assert.StartsWith("/rest/ping.view", request.PathAndQuery, StringComparison.Ordinal));
        gateway.Verify(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Lossless, null, true), Times.Once);
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubsonicTranscodeDecision_NativeSongIsRelayedWithTheClientProfile()
    {
        const string decision = """{"subsonic-response":{"status":"ok","version":"1.16.1","transcodeDecision":{"canDirectPlay":false,"canTranscode":true,"transcodeParams":"native"}}}""";
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            observed.Add(Observe(request));
            return request.RequestUri!.AbsolutePath == "/rest/ping.view"
                ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
                : Json(200, decision);
        });
        using var client = factory.CreateClient();
        const string path = "/rest/getTranscodeDecision.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f=json&mediaId=native-7&mediaType=song";

        using var response = await client.PostAsync(path,
            new StringContent(FeishinClientInfo, Encoding.UTF8, "application/json"));

        Assert.Equal(decision, await response.Content.ReadAsStringAsync());
        var relayed = observed[^1];
        Assert.Equal("POST", relayed.Method);
        Assert.Equal(path, relayed.PathAndQuery);
        Assert.Equal(FeishinClientInfo, relayed.Body);
    }

    [Fact]
    public async Task SubsonicTranscodeDecision_OwnedProviderSongAsksTheBackendAboutTheLocalCopy()
    {
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.ResolveKnownTrackAsync(It.IsAny<ProtocolExecutionContext>(), "ext-deezer-song-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync("native-7");
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            observed.Add(Observe(request));
            return request.RequestUri!.AbsolutePath == "/rest/ping.view"
                ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
                : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1","transcodeDecision":{"canDirectPlay":true,"canTranscode":true}}}""");
        }, services =>
        {
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/rest/getTranscodeDecision.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f=json&mediaId=ext-deezer-song-42&mediaType=song",
            new StringContent(FeishinClientInfo, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mediaId=native-7", observed[^1].PathAndQuery, StringComparison.Ordinal);
        Assert.Equal(FeishinClientInfo, observed[^1].Body);
        index.VerifyAll();
    }
}
