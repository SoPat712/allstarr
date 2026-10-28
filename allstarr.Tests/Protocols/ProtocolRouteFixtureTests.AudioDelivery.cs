using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
    [MediaToolsTheory]
    [InlineData("Feishin", "stream.mp3?audioCodec=mp3&static=false&playSessionId=feishin-ext-deezer-song-42-mp3-44100-0&audioBitRate=128000&audioSampleRate=44100", "flac", "flac", false)]
    [InlineData("Jellyfin Web", "universal?UserId=user&DeviceId=fixture&MaxStreamingBitrate=120000000&Container=mp3,mp4|aac,aac,ogg|opus,webm|opus&TranscodingContainer=ts&TranscodingProtocol=hls&AudioCodec=aac&MaxAudioSampleRate=48000&MaxAudioBitDepth=16&PlaySessionId=play&StartTimeTicks=0&EnableRedirection=true&EnableRemoteMedia=false&EnableAudioVbrEncoding=true", "flac", "flac", false)]
    [InlineData("Finamp", "universal?transcodingContainer=mp3&audioCodec=mp3&audioBitRate=128000&maxAudioChannels=2", "flac", "flac", false)]
    [InlineData("Finamp", "universal?transcodingContainer=mp3&audioCodec=mp3&audioBitRate=128000&maxAudioChannels=1", "mp3", "mp3", false)]
    [InlineData("Feishin", "stream.mp3?audioCodec=mp3&audioBitRate=64000", "mp3", "mp3", false)]
    [InlineData("Feishin", "stream.mp3?audioCodec=mp3&audioSampleRate=22050", "mp3", "mp3", false)]
    [InlineData("Feishin", "stream.mp3?audioCodec=mp3&static=false&playSessionId=feishin-ext-deezer-song-42-mp3-44100-0&audioBitRate=128000&audioSampleRate=44100", "mp3", "mp3", true)]
    [InlineData("Finamp", "universal?transcodingContainer=m4a&audioCodec=aac&audioBitRate=128000&maxAudioChannels=2", "m4a", "aac", true)]
    [InlineData("Jellyfin Web", "universal?Container=mp3,mp4|aac,aac,ogg|opus,webm|opus&AudioCodec=aac&MaxStreamingBitrate=128000&MaxAudioSampleRate=48000", "ogg", "opus", true)]
    public async Task JellyfinAudioClients_ReceiveCompatibleOriginalOrConvertedAudio(
        string clientName, string route, string container, string codec, bool fitsOriginal)
    {
        var gateway = DeliveryGateway(container, codec);
        using var factory = DeliveryFactory("Jellyfin", gateway);
        using var client = factory.CreateClient();
        var keyName = clientName == "Feishin" ? "apiKey" : "ApiKey";
        var path = $"/Audio/ext-deezer-song-42/{route}";
        path = clientName == "Feishin" && route.Contains("static=false", StringComparison.Ordinal)
            ? path.Replace("static=false", "static=false&apiKey=fixture-key", StringComparison.Ordinal)
            : $"{path}&{keyName}=fixture-key";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Emby-Authorization", $"MediaBrowser Client=\"{clientName}\", Device=\"Fixture\", DeviceId=\"fixture\", Version=\"1\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (fitsOriginal)
        {
            Assert.Equal(DeliveryBytes(codec), bytes);
            Assert.Equal(DeliveryMime(container), response.Content.Headers.ContentType?.MediaType);
        }
        else
        {
            Assert.NotEqual(DeliveryBytes(codec), bytes);
            var expected = clientName == "Jellyfin Web" ? "aac" : "mp3";
            Assert.Equal(expected, AudioMediaFacts.ReadPrefix(bytes, bytes.Length)!.Media.Codec);
            Assert.Equal(expected, response.Headers.GetValues("X-Allstarr-Audio-Codec").Single());
            Assert.Null(response.Content.Headers.ContentRange);
            Assert.Empty(response.Headers.AcceptRanges);
        }
    }

    [Theory]
    [InlineData("json", "mp3", 128, true)]
    [InlineData("xml", "mp3", 128, true)]
    [InlineData("json", "flac", 128, false)]
    [InlineData("xml", "flac", 128, false)]
    [InlineData("json", "mp3", 64, false)]
    [InlineData("xml", "mp3", 64, false)]
    public async Task FeishinTranscodeDecision_EvaluatesClientProfiles(string format, string codec, int bitrate, bool compatible)
    {
        var gateway = DeliveryGateway(codec, codec);
        using var factory = DeliveryFactory("Subsonic", gateway);
        using var client = factory.CreateClient();
        var profile = $$$$"""{"name":"Feishin","platform":"web","maxAudioBitrate":{{{{bitrate * 1000}}}},"maxTranscodingAudioBitrate":0,"codecProfiles":[],"directPlayProfiles":[{"containers":["mp3"],"audioCodecs":["mp3"],"protocols":["http"]}],"transcodingProfiles":[{"container":"mp3","audioCodec":"mp3","protocol":"http"}]}""";
        using var response = await client.PostAsync($"/rest/getTranscodeDecision.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}&mediaId=ext-deezer-song-42&mediaType=song",
            new StringContent(profile, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var decision = document.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision");
            Assert.Equal(compatible, decision.GetProperty("canDirectPlay").GetBoolean());
            Assert.Equal(!compatible, decision.GetProperty("canTranscode").GetBoolean());
            Assert.Equal(!compatible, decision.TryGetProperty("transcodeParams", out _));
            if (!compatible) Assert.Equal("mp3", decision.GetProperty("sourceStream").GetProperty("codec").GetString());
        }
        else
        {
            var decision = XDocument.Parse(body).Descendants().Single(element => element.Name.LocalName == "transcodeDecision");
            Assert.Equal(compatible ? "true" : "false", decision.Attribute("canDirectPlay")?.Value);
            Assert.Equal(compatible ? "false" : "true", decision.Attribute("canTranscode")?.Value);
            Assert.Equal(!compatible, decision.Attribute("transcodeParams") != null);
        }
    }

    [Theory]
    [InlineData("json", "AudioCodec", "audioChannels", "LessThanEqual", "1", false)]
    [InlineData("xml", "AudioCodec", "audioChannels", "LessThanEqual", "1", false)]
    [InlineData("json", "AudioCodec", "audioSamplerate", "LessThanEqual", "22050", false)]
    [InlineData("xml", "AudioCodec", "audioBitdepth", "LessThanEqual", "8", true)]
    [InlineData("json", "AudioCodec", "audioBitrate", "LessThanEqual", "128000", true)]
    public async Task FeishinTranscodeDecision_EnforcesCodecLimitations(string format, string type, string property, string comparison, string value, bool compatible)
    {
        using var factory = DeliveryFactory("Subsonic", DeliveryGateway("mp3", "mp3"));
        using var client = factory.CreateClient();
        var profile = $$$$"""{"name":"Feishin","platform":"web","maxAudioBitrate":0,"maxTranscodingAudioBitrate":0,"directPlayProfiles":[{"containers":["mp3"],"audioCodecs":["mp3"],"protocols":["http"]}],"codecProfiles":[{"type":"{{{{type}}}}","name":"mp3","limitations":[{"name":"{{{{property}}}}","comparison":"{{{{comparison}}}}","values":["{{{{value}}}}"],"required":true}]}],"transcodingProfiles":[{"audioCodec":"mp3","container":"mp3","protocol":"http"}]}""";
        using var response = await client.PostAsync($"/rest/getTranscodeDecision.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}&mediaId=ext-deezer-song-42&mediaType=song",
            new StringContent(profile, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(compatible, document.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision").GetProperty("canDirectPlay").GetBoolean());
        }
        else
            Assert.Equal(compatible ? "true" : "false", XDocument.Parse(body).Descendants()
                .Single(element => element.Name.LocalName == "transcodeDecision").Attribute("canDirectPlay")?.Value);
    }

    [MediaToolsTheory]
    [InlineData("json", "format=mp3&maxBitRate=128&estimateContentLength=true", "flac", false)]
    [InlineData("xml", "format=mp3&maxBitRate=128&estimateContentLength=true", "flac", false)]
    [InlineData("json", "format=mp3&maxBitRate=64", "mp3", false)]
    [InlineData("xml", "format=mp3&maxBitRate=64", "mp3", false)]
    [InlineData("json", "format=mp3&maxBitRate=128&estimateContentLength=true", "mp3", true)]
    [InlineData("xml", "format=raw&maxBitRate=0", "flac", true)]
    public async Task TempoStream_EnforcesFormatBitrateAndTimeOffset(string format, string query, string codec, bool compatible)
    {
        using var factory = DeliveryFactory("Subsonic", DeliveryGateway(codec, codec));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/stream.view?u=fixture&p=secret&v=1.16.1&c=tempo&f={format}&id=ext-deezer-song-42&{query}");
        if (compatible)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(DeliveryBytes(codec), await response.Content.ReadAsByteArrayAsync());
            return;
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var converted = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("mp3", AudioMediaFacts.ReadPrefix(converted, converted.Length)!.Media.Codec);
        Assert.Equal("mp3", response.Headers.GetValues("X-Allstarr-Audio-Codec").Single());
        Assert.NotEqual(DeliveryBytes(codec), converted);
    }

    [MediaToolsTheory]
    [InlineData("mp3", 0, true, true, true)]
    [InlineData("flac", 0, false, true, true)]
    [InlineData("mp3", 300000000, true, true, true)]
    [InlineData("mp3", 0, true, false, true)]
    [InlineData("mp3", 0, true, true, false)]
    [InlineData("mp3", 0, false, false, false)]
    public async Task JellyfinPlaybackInfo_AdvertisesTheDeliveredOriginalOrConversion(string codec, long offset, bool compatible, bool directPlay, bool directStream)
    {
        using var factory = DeliveryFactory("Jellyfin", DeliveryGateway(codec, codec));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/Items/ext-deezer-song-42/PlaybackInfo?ApiKey=fixture-key",
            new StringContent($$$$"""{"IsPlayback":true,"StartTimeTicks":{{{{offset}}}},"MaxStreamingBitrate":128000,"EnableDirectPlay":{{{{directPlay.ToString().ToLowerInvariant()}}}},"EnableDirectStream":{{{{directStream.ToString().ToLowerInvariant()}}}},"DeviceProfile":{"MaxStreamingBitrate":128000,"DirectPlayProfiles":[{"Type":"Audio","Container":"mp3","AudioCodec":"mp3"}],"CodecProfiles":[{"Type":"Audio","Codec":"mp3","Conditions":[{"Condition":"LessThanEqual","Property":"AudioChannels","Value":"2"},{"Condition":"LessThanEqual","Property":"AudioSampleRate","Value":"44100"}]}],"TranscodingProfiles":[{"Type":"Audio","Container":"ts","AudioCodec":"aac","Protocol":"hls"}]}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ErrorCode").ValueKind);
        var source = Assert.Single(root.GetProperty("MediaSources").EnumerateArray());
        Assert.Equal(compatible && directPlay, source.GetProperty("SupportsDirectPlay").GetBoolean());
        Assert.Equal(compatible && directStream, source.GetProperty("SupportsDirectStream").GetBoolean());
        Assert.Equal(!compatible, source.GetProperty("SupportsTranscoding").GetBoolean());
        Assert.Equal(!compatible, source.TryGetProperty("TranscodingUrl", out _));
        Assert.Equal(compatible ? codec : "mp3", source.GetProperty("Container").GetString());
        var url = source.GetProperty(compatible ? "DirectStreamUrl" : "TranscodingUrl").GetString()!;
        if (offset > 0)
        {
            Assert.Contains($"StartTimeTicks={offset}", url);
            return;
        }
        using var stream = await client.GetAsync(url + "&ApiKey=fixture-key");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var bytes = await stream.Content.ReadAsByteArrayAsync();
        if (compatible) Assert.Equal(DeliveryBytes(codec), bytes);
        else
        {
            Assert.False(source.TryGetProperty("DirectStreamUrl", out _));
            Assert.Equal("mp3", AudioMediaFacts.ReadPrefix(bytes, bytes.Length)!.Media.Codec);
            Assert.Equal("mp3", stream.Headers.GetValues("X-Allstarr-Audio-Codec").Single());
        }
    }

    [Fact]
    public async Task JellyfinPlaybackInfo_FlacUrlKeepsTheCompatibleLosslessSource()
    {
        var gateway = DeliveryGateway("flac", "flac");
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42",
                It.IsAny<ProviderAudioQuality>(), null, It.IsAny<bool>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string _, ProviderAudioQuality quality, string? _, bool _) =>
            {
                var lossless = quality is ProviderAudioQuality.Any or ProviderAudioQuality.Lossless;
                var codec = lossless ? "flac" : "mp3";
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DeliveryBytes(codec)) };
                response.Content.Headers.ContentType = new(DeliveryMime(codec));
                return new ProtocolProviderStream(response, new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"),
                    DateTimeOffset.UtcNow.AddMinutes(1), true, true, new(DeliveryMime(codec), codec, codec, lossless ? 500000 : 128000, 44100, 16, 2),
                    ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42");
            });
        using var factory = DeliveryFactory("Jellyfin", gateway);
        using var client = factory.CreateClient();
        using var info = await client.GetAsync("/Items/ext-deezer-song-42/PlaybackInfo?ApiKey=fixture-key&MaxStreamingBitrate=120000000");
        using var document = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        var source = document.RootElement.GetProperty("MediaSources")[0];
        Assert.True(source.GetProperty("SupportsDirectPlay").GetBoolean());
        using var stream = await client.GetAsync(source.GetProperty("DirectStreamUrl").GetString() + "&ApiKey=fixture-key");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal(DeliveryBytes("flac"), await stream.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task JellyfinNativePlaybackInfo_PreservesProfileBodyAndResponseBytes()
    {
        const string profile = """{ "StartTimeTicks":300000000, "DeviceProfile":{"DirectPlayProfiles":[],"TranscodingProfiles":[{"Type":"Audio","Container":"ts","AudioCodec":"aac"}]} }""";
        const string native = """{"MediaSources":[{"Id":"native","SupportsDirectPlay":false,"SupportsTranscoding":true,"TranscodingUrl":"native.m3u8","future":7}],"PlaySessionId":"native-play","ErrorCode":null}""";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"verified-user"}""");
            if (request.RequestUri.AbsolutePath == "/Items") return ItemLookup("""{"Id":"native","Type":"Audio"}""");
            Assert.Equal("/Items/native/PlaybackInfo?ApiKey=fixture-key", request.RequestUri.PathAndQuery);
            Assert.Equal(profile, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json(200, native);
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/Items/native/PlaybackInfo?ApiKey=fixture-key",
            new StringContent(profile, Encoding.UTF8, "application/json"));
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    }

    private static Mock<IProtocolProviderGateway> DeliveryGateway(string container, string codec)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-42", ExternalProvider = "deezer", ExternalId = "42", Title = "Fixture", Duration = 180 });
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<ProviderAudioQuality>(), null, It.IsAny<bool>()))
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DeliveryBytes(codec)) };
                response.Content.Headers.ContentType = new(DeliveryMime(container));
                return new ProtocolProviderStream(response, new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"),
                    DateTimeOffset.UtcNow.AddMinutes(1), true, true, DeliveryMedia(container, codec),
                    ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42");
            });
        return gateway;
    }

    private static readonly JsonElement DeliveryFixtures = ReadDeliveryFixtures();

    private static JsonElement ReadDeliveryFixtures()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Protocols", "audio-delivery.json")));
        return document.RootElement.Clone();
    }

    private static byte[] DeliveryBytes(string codec) => Convert.FromBase64String(
        DeliveryFixtures.GetProperty(codec).GetProperty("bytes").GetString()!);

    private static ProviderMediaFormat DeliveryMedia(string container, string codec)
    {
        var fixture = DeliveryFixtures.GetProperty(codec);
        int? Fact(string name) => fixture.GetProperty(name).ValueKind == JsonValueKind.Number ? fixture.GetProperty(name).GetInt32() : null;
        return new(DeliveryMime(container), container, codec, Fact("bitrate"), Fact("sampleRate"), Fact("bitDepth"), Fact("channels"));
    }

    private static string DeliveryMime(string container) => container switch
    {
        "flac" => "audio/flac",
        "m4a" => "audio/mp4",
        "opus" or "ogg" => "audio/ogg",
        _ => "audio/mpeg"
    };

    private static ProtocolFactory DeliveryFactory(string protocol, Mock<IProtocolProviderGateway> gateway) => new(protocol,
        request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"verified-user"}""")
            : request.RequestUri.AbsolutePath == "/rest/ping.view"
                ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
                : throw new InvalidOperationException($"Unexpected native request: {request.RequestUri}"), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
}
