using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [MediaToolsFact]
    public async Task ProviderTimeOffsets_BothRoutesServeRemainingTimelineAndRejectBeyondEnd()
    {
        var root = Directory.CreateTempSubdirectory("allstarr-offset-routes-").FullName;
        var path = Path.Combine(root, "cached.wav");
        var bytes = ProviderStreamOpenerTests.SeekWave();
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            foreach (var (protocol, format, alias) in new[] { ("Jellyfin", "json", false), ("Subsonic", "json", false), ("Subsonic", "xml", false), ("Subsonic", "json", true) })
            {
                var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
                gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "6764050144", ProviderAudioQuality.Any, null, false))
                    .ReturnsAsync(() =>
                    {
                        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) };
                        response.Content.Headers.ContentType = new("audio/wav");
                        response.Content.Headers.ContentLength = bytes.Length;
                        return new ProtocolProviderStream(response, new("cache", new Uri("https://cache.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                            true, true, new("audio/wav", "wav", "pcm_s16le"), ProviderStreamRetryBehavior.DoNotRetry), "apple-download", "6764050144", IsCached: true);
                    });
                gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "6764050144"))
                    .ReturnsAsync(new Song { Title = "Housewife", Artist = "Tate McRae", Duration = 40 });
                using var factory = new ProtocolFactory(protocol, request => request.RequestUri!.AbsolutePath == "/Users/Me"
                    ? Json(200, """{"Id":"verified-user"}""") : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""), services =>
                    {
                        services.RemoveAll<IProtocolProviderGateway>();
                        services.AddSingleton(gateway.Object);
                    });
                using var client = factory.CreateClient();
                var offsetName = alias ? "offset" : "timeOffset";
                var route = protocol == "Jellyfin"
                    ? "/Audio/ext-apple-download-song-6764050144/stream?api_key=fixture-key"
                    : "/rest/stream.view?id=ext-apple-download-song-6764050144&u=fixture&p=secret&v=1.16.1&c=Musiver&f=" + format;
                using var start = await client.GetAsync(route);
                Assert.Equal(HttpStatusCode.OK, start.StatusCode);
                Assert.Equal(bytes, await start.Content.ReadAsByteArrayAsync());
                var offset = protocol == "Jellyfin" ? "StartTimeTicks=360000000" : offsetName + "=36";
                using var seek = await client.GetAsync(route + "&" + offset);
                Assert.Equal(HttpStatusCode.OK, seek.StatusCode);
                Assert.Equal("36", Assert.Single(seek.Headers.GetValues("X-Allstarr-Start-Seconds")));
                Assert.Equal("40", Assert.Single(seek.Headers.GetValues("X-Content-Duration")));
                var remaining = await seek.Content.ReadAsByteArrayAsync();
                Assert.Equal(remaining.Length, seek.Content.Headers.ContentLength);
                Assert.InRange(remaining.Length, 64000, 65000);
                var output = Path.Combine(root, "remaining.wav");
                await File.WriteAllBytesAsync(output, remaining);
                Assert.InRange((await AudioMediaFacts.ReadFileAsync(output, default))!.DurationSeconds!.Value, 3.9, 4.1);
                var outside = protocol == "Jellyfin" ? "StartTimeTicks=400000000" : offsetName + "=40";
                using var invalid = await client.GetAsync(route + "&" + outside);
                Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
                Assert.Contains("outside the audio timeline", await invalid.Content.ReadAsStringAsync());
                var malformed = protocol == "Jellyfin" ? "StartTimeTicks=-1" : offsetName + "=-1";
                using var negative = await client.GetAsync(route + "&" + malformed);
                Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
                using var range = new HttpRequestMessage(HttpMethod.Get, route + "&" + offset);
                range.Headers.Range = new(0, 99);
                using var partial = await client.SendAsync(range);
                Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
                Assert.Equal(remaining[..100], await partial.Content.ReadAsByteArrayAsync());
                Assert.Equal(remaining.Length, partial.Content.Headers.ContentRange?.Length);
                if (protocol == "Jellyfin")
                {
                    using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, route + "&" + offset));
                    Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                    Assert.Equal(remaining.Length, head.Content.Headers.ContentLength);
                    Assert.Empty(await head.Content.ReadAsByteArrayAsync());
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
