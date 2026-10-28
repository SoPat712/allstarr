using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private static readonly JsonElement ClientAudioProfiles = ReadClientAudioProfiles();

    public static IEnumerable<object[]> PlaybackInfoClientProfiles()
    {
        foreach (var profile in new[] { "web", "finamp", "default" })
            foreach (var codec in new[] { "flac", "mp3", "aac" })
                foreach (var knownFacts in new[] { false, true })
                    yield return [profile, codec, knownFacts];
    }

    [MediaToolsTheory]
    [MemberData(nameof(PlaybackInfoClientProfiles))]
    public async Task JellyfinPlaybackInfo_RealClientRequestsAllowSupportedOriginalAudio(string profileName, string codec, bool knownFacts)
    {
        var container = codec == "aac" ? "m4a" : codec;
        using var factory = DeliveryFactory("Jellyfin", ClientProfileGateway(container, codec, knownFacts));
        using var client = factory.CreateClient();
        var profile = ClientAudioProfiles.GetProperty(profileName);
        const string path = "/Items/ext-applemusic-song-42/PlaybackInfo?ApiKey=fixture-key";
        using var response = profileName == "finamp"
            ? await client.GetAsync(path + "&" + profile.GetProperty("query").GetString())
            : await client.PostAsync(path, new StringContent(profileName == "web"
                ? JsonSerializer.Serialize(new { IsPlayback = true, MaxStreamingBitrate = 140000000, DeviceProfile = profile.GetProperty("deviceProfile") })
                : profile.GetProperty("body").GetRawText(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ErrorCode").ValueKind);
        var source = Assert.Single(root.GetProperty("MediaSources").EnumerateArray());
        var converts = codec == "flac" && !knownFacts && profileName != "finamp";
        Assert.Equal(!converts, source.GetProperty("SupportsDirectPlay").GetBoolean());
        Assert.Equal(!converts, source.GetProperty("SupportsDirectStream").GetBoolean());
        Assert.Equal(converts, source.GetProperty("SupportsTranscoding").GetBoolean());
        Assert.Equal(converts, source.TryGetProperty("TranscodingUrl", out _));
        Assert.Equal(container, source.GetProperty("Container").GetString());
        var audio = Assert.Single(source.GetProperty("MediaStreams").EnumerateArray(),
            stream => stream.GetProperty("Type").GetString() == "Audio");
        Assert.Equal(codec, audio.GetProperty("Codec").GetString());
        if (!knownFacts)
        {
            Assert.False(source.TryGetProperty("Bitrate", out _));
            Assert.False(audio.TryGetProperty("SampleRate", out _));
            Assert.False(audio.TryGetProperty("BitDepth", out _));
            Assert.Equal(converts && profileName == "web", audio.TryGetProperty("Channels", out _));
            if (converts && profileName == "web") Assert.Equal(2, audio.GetProperty("Channels").GetInt32());
        }
        using var stream = await client.GetAsync(source.GetProperty(converts ? "TranscodingUrl" : "DirectStreamUrl").GetString() + "&ApiKey=fixture-key");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var delivered = await stream.Content.ReadAsByteArrayAsync();
        if (converts) Assert.Equal("flac", AudioMediaFacts.ReadPrefix(delivered, delivered.Length)!.Media.Codec);
        else Assert.Equal(DeliveryBytes(codec), delivered);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task JellyfinPlaybackInfo_CommaSeparatedContainersKeepUnknownBitrateFlacLossless(bool explicitFlags, bool emptyCodec)
    {
        using var factory = DeliveryFactory("Jellyfin", ClientProfileGateway("flac", "flac", false));
        using var client = factory.CreateClient();
        var body = JsonNode.Parse("""{"UserId":"fixture-user","MaxStreamingBitrate":140000000,"IsPlayback":true,"DeviceProfile":{"DirectPlayProfiles":[{"Type":"Audio","Container":"flac,mp3,m4a"}],"TranscodingProfiles":[{"Type":"Audio","Container":"mp3","AudioCodec":"mp3","Protocol":"http"}]}}""")!;
        if (explicitFlags)
        {
            body["EnableDirectPlay"] = true;
            body["EnableDirectStream"] = true;
        }
        if (emptyCodec) body["DeviceProfile"]!["DirectPlayProfiles"]![0]!["AudioCodec"] = "";
        using var response = await client.PostAsync("/Items/ext-applemusic-song-42/PlaybackInfo?UserId=fixture-user&ApiKey=fixture-key",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("ErrorCode").ValueKind);
        var source = Assert.Single(document.RootElement.GetProperty("MediaSources").EnumerateArray());
        Assert.False(source.GetProperty("SupportsDirectPlay").GetBoolean());
        Assert.False(source.GetProperty("SupportsDirectStream").GetBoolean());
        Assert.True(source.GetProperty("SupportsTranscoding").GetBoolean());
        Assert.Equal("flac", source.GetProperty("Container").GetString());
    }

    [MediaToolsTheory]
    [InlineData("flac", false, true)]
    [InlineData("flac", true, true)]
    [InlineData("mp3", false, true)]
    [InlineData("mp3", true, true)]
    [InlineData("aac", false, false)]
    [InlineData("aac", true, false)]
    public async Task FeishinJellyfin_RealUniversalRequestsMatchSourceFormat(string codec, bool knownFacts, bool compatible)
    {
        using var factory = DeliveryFactory("Jellyfin", ClientProfileGateway(codec == "aac" ? "m4a" : codec, codec, knownFacts));
        using var client = factory.CreateClient();
        var query = ClientAudioProfiles.GetProperty("feishinJellyfin").GetProperty("universalQuery").GetString()!
            .Replace("{codec}", codec, StringComparison.Ordinal).Replace("{id}", "ext-applemusic-song-42", StringComparison.Ordinal);
        using var response = await client.GetAsync($"/Audio/ext-applemusic-song-42/universal?{query}&apiKey=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (compatible && (codec != "flac" || knownFacts)) Assert.Equal(DeliveryBytes(codec), await response.Content.ReadAsByteArrayAsync());
        else if (compatible) Assert.Equal("flac", AudioMediaFacts.ReadPrefix(await response.Content.ReadAsByteArrayAsync(), null)!.Media.Codec);
        else
        {
            var converted = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal("aac", AudioMediaFacts.ReadPrefix(converted, converted.Length)!.Media.Codec);
            Assert.Equal("audio/aac", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Theory]
    [InlineData("finamp", "flac")]
    [InlineData("finamp", "mp3")]
    [InlineData("finamp", "aac")]
    [InlineData("feishinJellyfin", "flac")]
    [InlineData("feishinJellyfin", "mp3")]
    [InlineData("feishinJellyfin", "aac")]
    public async Task JellyfinClients_RealOriginalPlaybackRoutesKeepSourceBytes(string profileName, string codec)
    {
        using var factory = DeliveryFactory("Jellyfin", ClientProfileGateway(codec == "aac" ? "m4a" : codec, codec, false));
        using var client = factory.CreateClient();
        var route = ClientAudioProfiles.GetProperty(profileName).GetProperty("originalRoute").GetString()!
            .Replace("{id}", "ext-applemusic-song-42", StringComparison.Ordinal);
        using var response = await client.GetAsync(route + "?ApiKey=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DeliveryBytes(codec), await response.Content.ReadAsByteArrayAsync());
    }

    public static IEnumerable<object[]> SubsonicClientProfiles()
    {
        foreach (var format in new[] { "json", "xml" })
            foreach (var codec in new[] { "flac", "mp3", "aac" })
                foreach (var knownFacts in new[] { false, true })
                    yield return [format, codec, knownFacts];
    }

    [Theory]
    [MemberData(nameof(SubsonicClientProfiles))]
    public async Task FeishinSubsonic_RealClientInfoAllowsSupportedOriginalAudio(string format, string codec, bool knownFacts)
    {
        using var factory = DeliveryFactory("Subsonic", ClientProfileGateway(codec == "aac" ? "m4a" : codec, codec, knownFacts));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync($"/rest/getTranscodeDecision.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}&mediaId=ext-applemusic-song-42&mediaType=song",
            new StringContent(ClientAudioProfiles.GetProperty("feishinSubsonic").GetProperty("clientInfo").GetRawText(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var decision = document.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision");
            Assert.True(decision.GetProperty("canDirectPlay").GetBoolean());
            Assert.False(decision.GetProperty("canTranscode").GetBoolean());
            Assert.False(decision.TryGetProperty("transcodeParams", out _));
            Assert.False(decision.TryGetProperty("errorReason", out _));
            Assert.Equal(codec, decision.GetProperty("sourceStream").GetProperty("codec").GetString());
        }
        else
        {
            var decision = XDocument.Parse(body).Descendants().Single(element => element.Name.LocalName == "transcodeDecision");
            Assert.Equal("true", decision.Attribute("canDirectPlay")?.Value);
            Assert.Equal("false", decision.Attribute("canTranscode")?.Value);
            Assert.Null(decision.Attribute("transcodeParams"));
            Assert.Null(decision.Attribute("errorReason"));
        }
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    public async Task FeishinSubsonic_ClientInfoLimitsConvertUnknownBitratesAndKnownViolations(string format, bool knownFacts)
    {
        using var factory = DeliveryFactory("Subsonic", ClientProfileGateway("flac", "flac", knownFacts));
        using var client = factory.CreateClient();
        var profile = JsonNode.Parse(ClientAudioProfiles.GetProperty("feishinSubsonic").GetProperty("clientInfo").GetRawText())!;
        profile["maxAudioBitrate"] = 140000000;
        profile["codecProfiles"] = JsonNode.Parse("""[{"type":"AudioCodec","name":"flac","limitations":[{"name":"audioBitdepth","comparison":"LessThanEqual","values":["8"],"required":true}]}]""");
        using var response = await client.PostAsync($"/rest/getTranscodeDecision.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}&mediaId=ext-applemusic-song-42&mediaType=song",
            new StringContent(profile.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var decision = document.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision");
            Assert.False(decision.GetProperty("canDirectPlay").GetBoolean());
            Assert.True(decision.GetProperty("canTranscode").GetBoolean());
            Assert.True(decision.TryGetProperty("transcodeParams", out _));
        }
        else
        {
            var decision = XDocument.Parse(body).Descendants().Single(element => element.Name.LocalName == "transcodeDecision");
            Assert.Equal("false", decision.Attribute("canDirectPlay")?.Value);
            Assert.Equal("true", decision.Attribute("canTranscode")?.Value);
            Assert.NotNull(decision.Attribute("transcodeParams"));
        }
    }

    private static JsonElement ReadClientAudioProfiles()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Protocols", "client-audio-profiles.json")));
        return document.RootElement.Clone();
    }

    private static Mock<IProtocolProviderGateway> ClientProfileGateway(string container, string codec, bool knownFacts)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "applemusic", "42"))
            .ReturnsAsync(new Song { Id = "ext-applemusic-song-42", ExternalProvider = "applemusic", ExternalId = "42", Title = "Fixture", Duration = 180 });
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "applemusic", "42", It.IsAny<ProviderAudioQuality>(), null, It.IsAny<bool>()))
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DeliveryBytes(codec)) };
                response.Content.Headers.ContentType = new(DeliveryMime(container));
                ProviderMediaFormat media = knownFacts ? DeliveryMedia(container, codec) : new(DeliveryMime(container), container, codec);
                return new ProtocolProviderStream(response, new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"),
                    DateTimeOffset.UtcNow.AddMinutes(1), true, true, media, ProviderStreamRetryBehavior.DoNotRetry), "applemusic", "42");
            });
        return gateway;
    }
}
