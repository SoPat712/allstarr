using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderAudioTranscoderTests
{
    [Theory]
    [InlineData("aac", 256000, 256000)]
    [InlineData("aac", 500000, 500000)]
    [InlineData("mp3", 300000, 256000)]
    public void ConversionUsesTheBestAcceptedBitrateWithinTheRequestedCap(string codec, int cap, int expected)
    {
        var request = new ProviderAudioDeliveryRequest
        { MaxBitrate = cap, Formats = [new([codec], [codec], ["http"])] };
        Assert.Equal(expected, ProviderAudioTranscoder.Plan(request, WaveMedia)!.Media.Bitrate);
    }

    [Theory]
    [InlineData("flac", "flac")]
    [InlineData("alac", "mp4")]
    public void GenerousBitrateCapsKeepUnknownLosslessPropertiesInherited(string codec, string container)
    {
        var request = new ProviderAudioDeliveryRequest { MaxBitrate = 140000000 };
        var source = new ProviderMediaFormat(container == "flac" ? "audio/flac" : "audio/mp4", container, codec);

        var plan = Assert.IsType<ProviderAudioConversion>(ProviderAudioTranscoder.Plan(request, source));

        Assert.Equal(codec, plan.Media.Codec);
        Assert.Null(plan.Media.SampleRate);
        Assert.Null(plan.Media.Channels);
        Assert.Null(plan.Media.BitDepth);
        Assert.Null(plan.Media.Bitrate);
        Assert.Equal("aac", ProviderAudioTranscoder.Plan(request with { MaxBitrate = 128000 }, source)!.Media.Codec);
        Assert.Null(ProviderAudioTranscoder.Plan(request,
            new("audio/flac", "flac", "flac", sampleRate: 96000, bitDepth: 24, channels: 2)));
        var cappedMp3 = ProviderAudioTranscoder.Plan(request with { MaxBitrate = 128000 }, new("audio/mpeg", "mp3", "mp3"));
        Assert.Equal("aac", cappedMp3!.Media.Codec);
        Assert.Equal(128000, cappedMp3.Media.Bitrate);
    }

    [MediaToolsFact]
    public async Task LosslessConversionInheritsTheActualHighResolutionMonoFormat()
    {
        var directory = Directory.CreateTempSubdirectory("allstarr-inherited-audio-").FullName;
        try
        {
            var input = Path.Combine(directory, "input.wav");
            var original = Path.Combine(directory, "source.flac");
            var converted = Path.Combine(directory, "converted.flac");
            await File.WriteAllBytesAsync(input, Wave(2));
            await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-y", "-i", input, "-threads", "1", "-ar", "96000", "-ac", "1", "-sample_fmt", "s32", "-c:a", "flac", original]);
            var request = new ProviderAudioDeliveryRequest { MaxBitrate = 140000000 };
            var plan = ProviderAudioTranscoder.Plan(request, new("audio/flac", "flac", "flac"))!;
            using var response = Source(await File.ReadAllBytesAsync(original));
            await ProviderAudioTranscoder.ApplyAsync(response, new DefaultHttpContext().Response, plan, null, default, durationSeconds: 2);
            await File.WriteAllBytesAsync(converted, await response.Content.ReadAsByteArrayAsync());
            var facts = await AudioMediaFacts.ReadFileAsync(converted, default);
            Assert.Equal(96000, facts!.Media.SampleRate);
            Assert.Equal(1, facts.Media.Channels);
            Assert.Equal(24, facts.Media.BitDepth);
            Task<string> Samples(string path) => ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-i", path, "-threads", "1", "-c:a", "pcm_s24le", "-f", "md5", "-"]);
            Assert.Equal(await Samples(original), await Samples(converted));
        }
        finally { Directory.Delete(directory, true); }
    }

    [MediaToolsFact]
    public async Task UnknownLengthTruncatedAudioCannotCompleteTheExpectedTimeline()
    {
        var bytes = Wave(8)[..(44 + 44100 * 4 * 4)];
        using var input = new TailStream(bytes, int.MaxValue);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) };
        Assert.Null(response.Content.Headers.ContentLength);
        var gate = new MediaConversionGate(new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [MediaCapacityLimits.ConversionsKey] = "1" }).Build()));

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await ProviderAudioTranscoder.ApplyAsync(response, new DefaultHttpContext().Response,
                ProviderAudioTranscoder.Plan(Request(ProtocolKind.Jellyfin), WaveMedia)!, gate, default, durationSeconds: 8);
            await (await response.Content.ReadAsStreamAsync()).CopyToAsync(Stream.Null);
        });

        using var available = await gate.EnterAsync(default);
    }

    [Fact]
    public void ExplicitBitrateCapsConvertUnknownBitratesWithoutRejectingUncappedPlayback()
    {
        var unknown = new ProviderMediaFormat("audio/mpeg", "mp3", "mp3", sampleRate: 44100, channels: 2);
        Assert.True(new ProviderAudioDeliveryRequest().Evaluate(unknown).Supported);
        var request = new ProviderAudioDeliveryRequest { MaxBitrate = 128000 };
        Assert.False(request.Evaluate(unknown).Supported);
        var decision = request.Decide(unknown);
        Assert.True(decision.SupportsTranscoding);
        Assert.Equal(128000, decision.Media!.Bitrate);
        Assert.True(request.Evaluate(new("audio/mpeg", "mp3", "mp3", 96000, 44100, channels: 2)).SupportsDirectPlay);
    }

    private static readonly ProviderMediaFormat WaveMedia = new("audio/wav", "wav", "pcm_s16le", 1411200, 44100, 16, 2);

    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin, "aac", "aac")]
    [InlineData(ProtocolKind.Subsonic, "mp3", "mp3")]
    public async Task BitrateLimitConvertsRealAudioAndReportsDeliveredFacts(ProtocolKind protocol, string codec, string container)
    {
        var request = Request(protocol);
        var plan = Assert.IsType<ProviderAudioConversion>(ProviderAudioTranscoder.Plan(request, WaveMedia));
        Assert.Equal(codec, plan.Media.Codec);
        Assert.Equal(container, plan.Media.Container);
        Assert.Equal(128000, plan.Media.Bitrate);
        var decision = request.Decide(WaveMedia, 1234);
        Assert.True(decision.Supported);
        Assert.True(decision.SupportsTranscoding);
        Assert.False(decision.SupportsDirectPlay);
        Assert.Null(decision.Size);
        using var response = Source(Wave(4));
        response.Headers.ETag = new("\"original\"");
        response.Headers.AcceptRanges.Add("bytes");
        var http = new DefaultHttpContext();

        await ProviderAudioTranscoder.ApplyAsync(response, http.Response, plan, null, default);
        Assert.Null(response.Content.Headers.ContentLength);
        var output = await response.Content.ReadAsByteArrayAsync();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "." + container);
        try
        {
            await File.WriteAllBytesAsync(path, output);
            var facts = await AudioMediaFacts.ReadFileAsync(path, default);
            Assert.Equal(codec, facts!.Media.Codec);
            Assert.Equal(plan.Media.SampleRate, facts.Media.SampleRate);
            Assert.Equal(plan.Media.Channels, facts.Media.Channels);
            Assert.InRange(output.Length * 8d / 4, 100000, 155000);
        }
        finally { File.Delete(path); }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.Null(response.Headers.ETag);
        Assert.Empty(response.Headers.AcceptRanges);
        Assert.Equal(plan.Media.MimeType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(codec, http.Response.Headers["X-Allstarr-Audio-Codec"].ToString());
    }

    [MediaToolsFact]
    public async Task TranscodingTargetsAndCodecConditionsProduceAcceptedAudio()
    {
        using var document = JsonDocument.Parse("""{"EnableDirectPlay":false,"EnableDirectStream":false,"DeviceProfile":{"DirectPlayProfiles":[{"Type":"Audio","Container":"flac","AudioCodec":"flac"}],"TranscodingProfiles":[{"Type":"Audio","Container":"mp4","AudioCodec":"aac","Protocol":"http"}],"CodecProfiles":[{"Type":"Audio","Codec":"aac","Conditions":[{"Property":"AudioSampleRate","Condition":"LessThanEqual","Value":"24000"},{"Property":"AudioChannels","Condition":"Equals","Value":"1"}]}]}}""");
        var request = ProviderAudioDeliveryRequests.Jellyfin(Query(("MaxStreamingBitrate", "128000")), document.RootElement);
        var plan = Assert.IsType<ProviderAudioConversion>(ProviderAudioTranscoder.Plan(request, new("audio/flac", "flac", "flac", 900000, 96000, 24, 6)));
        Assert.Equal("aac", plan.Media.Codec);
        Assert.Equal("mp4", plan.Media.Container);
        Assert.InRange(plan.Media.SampleRate!.Value, 8000, 24000);
        Assert.Equal(1, plan.Media.Channels);
        Assert.True(request.Decide(WaveMedia).SupportsTranscoding);
        using var response = Source(Wave(2));
        await ProviderAudioTranscoder.ApplyAsync(response, new DefaultHttpContext().Response, plan, null, default);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".m4a");
        try
        {
            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
            var facts = await AudioMediaFacts.ReadFileAsync(path, default);
            Assert.Equal("aac", facts!.Media.Codec);
            Assert.Equal(plan.Media.SampleRate, facts.Media.SampleRate);
            Assert.Equal(1, facts.Media.Channels);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "aac")]
    [InlineData(ProtocolKind.Subsonic, "mp3")]
    public async Task InspectionDescribesConversionAndDisposesItsReadOnlySource(ProtocolKind protocol, string codec)
    {
        using var body = new MemoryStream([1, 2, 3]);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly && context.AudioDelivery == null),
                "fixture", "one", It.IsAny<ProviderAudioQuality>(), null, true))
            .ReturnsAsync(new ProtocolProviderStream(new(HttpStatusCode.OK) { Content = new StreamContent(body) }, Lease(), "fixture", "one"));
        var context = new ProtocolExecutionContext(protocol, "backend", "principal", null, "inspection", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var decision = await new ProviderStreamOpener(gateway.Object, new()).InspectAsync(context, "fixture", "one", Request(protocol));
        Assert.True(decision.SupportsTranscoding);
        Assert.False(decision.SupportsDirectPlay);
        Assert.False(decision.SupportsDirectStream);
        Assert.Equal(codec, decision.Media!.Codec);
        Assert.Equal(128000, decision.Media.Bitrate);
        Assert.Null(decision.Size);
        Assert.False(body.CanRead);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [MediaToolsTheory]
    [InlineData("m4a", "aac", "mp4")]
    [InlineData("ogg", "opus", "ogg")]
    public async Task ExplicitAcceptedTargetsEncodeTheirAdvertisedContainer(string requested, string codec, string container)
    {
        var request = ProviderAudioDeliveryRequests.Jellyfin(Query(("Container", requested), ("AudioCodec", codec), ("MaxStreamingBitrate", "128000")));
        var plan = Assert.IsType<ProviderAudioConversion>(ProviderAudioTranscoder.Plan(request, WaveMedia));
        Assert.Equal(codec, plan.Media.Codec);
        Assert.Equal(container, plan.Media.Container);
        using var response = Source(Wave(4));
        await ProviderAudioTranscoder.ApplyAsync(response, new DefaultHttpContext().Response, plan, null, default);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "." + container);
        try
        {
            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
            var facts = await AudioMediaFacts.ReadFileAsync(path, default);
            Assert.Equal(codec, facts!.Media.Codec);
            Assert.Equal(container, facts.Media.Container);
            Assert.Equal(plan.Media.SampleRate, facts.Media.SampleRate);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ContradictoryTargetsHaveNoConversionPlan()
    {
        var request = new ProviderAudioDeliveryRequest
        {
            Profiles = [new(["aac"], ["aac"], ["http"])],
            MaxSampleRate = 22050,
            Conditions = [new("aac", "AudioSampleRate", "GreaterThanEqual", ["44100"], true)]
        };
        Assert.Null(ProviderAudioTranscoder.Plan(request, WaveMedia));
        Assert.False(request.Decide(WaveMedia).Supported);
        Assert.Null(ProviderAudioTranscoder.Plan(new() { Formats = [new(["unsupported"], ["unsupported"], ["http"])] }, WaveMedia));
        Assert.Null(ProviderAudioTranscoder.Plan(new() { EnableDirectPlay = false, EnableDirectStream = false, EnableTranscoding = false }, WaveMedia));
        Assert.Null(ProviderAudioTranscoder.Plan(new()
        {
            Profiles = [new(["aac"], ["aac"], ["http"])],
            Conditions = [new("aac", "AudioProfile", "Equals", ["HE-AAC"], true)]
        }, WaveMedia));
    }

    [Theory]
    [InlineData("mp4", "aac", true)]
    [InlineData("ts", "aac", false)]
    [InlineData("mp4", "opus", false)]
    public void HlsOnlyProfilesRequireAcceptedAacMp4Targets(string container, string codec, bool supported)
    {
        using var profile = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            DeviceProfile = new
            {
                DirectPlayProfiles = Array.Empty<object>(),
                TranscodingProfiles = new[] { new { Type = "Audio", Container = container, AudioCodec = codec, Protocol = "hls" } }
            }
        }));
        var request = ProviderAudioDeliveryRequests.Jellyfin(Query(("MaxStreamingBitrate", "128000")), profile.RootElement);
        var plan = ProviderAudioTranscoder.Plan(request, WaveMedia);
        Assert.Equal(supported, plan != null);
        Assert.Equal(supported, request.Decide(WaveMedia).Hls);
        if (supported) Assert.True(plan!.Hls);
    }

    [MediaToolsFact]
    public async Task FailedEncoderStartupDisposesInputAndReleasesCapacityBeforeHeaders()
    {
        using var input = new MemoryStream([1, 2, 3]);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) };
        var gate = new MediaConversionGate(new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [MediaCapacityLimits.ConversionsKey] = "1" }).Build()));
        var http = new DefaultHttpContext();
        await Assert.ThrowsAsync<IOException>(() => ProviderAudioTranscoder.ApplyAsync(response, http.Response,
            ProviderAudioTranscoder.Plan(Request(ProtocolKind.Jellyfin), WaveMedia)!, gate, default));
        Assert.False(http.Response.HasStarted);
        Assert.False(input.CanRead);
        using var available = await gate.EnterAsync(default);
    }

    [MediaToolsFact]
    public async Task ConversionAppliesTheTimeOffsetToTheDeliveredAudioTimeline()
    {
        var plan = ProviderAudioTranscoder.Plan(Request(ProtocolKind.Jellyfin), WaveMedia)!;
        using var response = Source(Wave(4));
        var http = new DefaultHttpContext();
        await ProviderAudioTranscoder.ApplyAsync(response, http.Response, plan, null, default, 2 * TimeSpan.TicksPerSecond, 4);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".aac");
        try
        {
            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
            var facts = await AudioMediaFacts.ReadFileAsync(path, default);
            Assert.InRange(facts!.DurationSeconds!.Value, 1.8, 2.3);
            Assert.Equal("2", http.Response.Headers["X-Allstarr-Start-Seconds"].ToString());
            Assert.Equal("4", http.Response.Headers["X-Content-Duration"].ToString());
        }
        finally { File.Delete(path); }
        using var invalid = Source(Wave(1));
        var outside = await Assert.ThrowsAsync<ProviderAudioOffsetException>(() => ProviderAudioTranscoder.ApplyAsync(
            invalid, new DefaultHttpContext().Response, plan, null, default, 4 * TimeSpan.TicksPerSecond, 4));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, outside.StatusCode);
    }

    [Fact]
    public async Task ConvertedHeadDescribesTheOutputWithoutStartingAProcess()
    {
        var gate = new MediaConversionGate(new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [MediaCapacityLimits.ConversionsKey] = "1" }).Build()));
        using var busy = await gate.EnterAsync(default);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "one", It.IsAny<ProviderAudioQuality>(), null, true))
            .ReturnsAsync(new ProtocolProviderStream(Source([1, 2, 3]), Lease(), "fixture", "one"));
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal", null, "conversion", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Head;
        var result = await new ProviderStreamOpener(gateway.Object, new(), conversions: gate)
            .OpenAsync(http, context, "fixture", "one", ProviderAudioQuality.Any, Request(ProtocolKind.Jellyfin));
        Assert.IsType<EmptyResult>(result);
        Assert.Equal("audio/aac", http.Response.ContentType);
        Assert.Null(http.Response.ContentLength);
        Assert.Equal("aac", http.Response.Headers["X-Allstarr-Audio-Codec"].ToString());
    }

    [Fact]
    public async Task FittingOriginalUsesExactBytesWithoutConversionCapacity()
    {
        var limits = new MediaCapacityLimits(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [MediaCapacityLimits.ConversionsKey] = "1" }).Build());
        var gate = new MediaConversionGate(limits);
        using var busy = await gate.EnterAsync(default);
        var bytes = Wave(1);
        var request = new ProviderAudioDeliveryRequest();
        Assert.Null(ProviderAudioTranscoder.Plan(request, WaveMedia));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "one", ProviderAudioQuality.Any, null, false))
            .ReturnsAsync(new ProtocolProviderStream(Source(bytes), Lease(), "fixture", "one"));
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal", null, "conversion", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var result = await new ProviderStreamOpener(gateway.Object, new(), conversions: gate)
            .OpenAsync(new DefaultHttpContext(), context, "fixture", "one", ProviderAudioQuality.Any, request);
        using var output = new MemoryStream();
        using var stream = Assert.IsType<FileStreamResult>(result).FileStream;
        await stream.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
    }

    [MediaToolsFact]
    public async Task ProgressiveConversionEmitsAudioBeforeTheInputTailAndReleasesCapacityOnAbort()
    {
        using var tail = new TailStream(Wave(8), 44100 * 4 * 3 + 44);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(tail) };
        var gate = new MediaConversionGate(new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [MediaCapacityLimits.ConversionsKey] = "1" }).Build()));
        using var aborted = new CancellationTokenSource();
        var plan = ProviderAudioTranscoder.Plan(Request(ProtocolKind.Jellyfin), WaveMedia)!;
        await ProviderAudioTranscoder.ApplyAsync(response, new DefaultHttpContext().Response, plan, gate, aborted.Token);
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.True(await stream.ReadAsync(new byte[4096]) > 0);
        Assert.False(tail.Released);
        await Assert.ThrowsAsync<LocalCapacityException>(async () => { using var unavailable = await gate.EnterAsync(default); });
        aborted.Cancel();
        response.Dispose();
        using var available = await gate.EnterAsync(default);
    }

    [MediaToolsFact]
    public async Task UncachedTimeOffsetProducesAudioBeforeTheOriginalTailCompletes()
    {
        using var tail = new TailStream(Wave(8), 44100 * 4 * 3 + 44);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(tail) };
        var http = new DefaultHttpContext();
        using var aborted = new CancellationTokenSource();
        var gate = new MediaConversionGate(new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [MediaCapacityLimits.ConversionsKey] = "1" }).Build()));

        await ProviderAudioTimeSeek.ApplyAsync(response, http.Response, TimeSpan.TicksPerSecond, gate, aborted.Token, WaveMedia, 8);

        Assert.False(tail.Released);
        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Equal("1", http.Response.Headers["X-Allstarr-Start-Seconds"].ToString());
        Assert.True(await (await response.Content.ReadAsStreamAsync()).ReadAsync(new byte[4096]) > 0);
        await aborted.CancelAsync();
        response.Dispose();
        using var available = await gate.EnterAsync(default);
    }

    [Fact]
    public async Task ZeroTimeOffsetKeepsTheOriginalWithoutConversionCapacity()
    {
        var bytes = Wave(1);
        using var response = Source(bytes);
        var gate = new MediaConversionGate(new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [MediaCapacityLimits.ConversionsKey] = "1" }).Build()));
        using var busy = await gate.EnterAsync(default);
        await ProviderAudioTimeSeek.ApplyAsync(response, new DefaultHttpContext().Response, 0, gate, default, WaveMedia, 1);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [MediaToolsFact]
    public async Task ConvertedRangesReopenTheFullOriginalAndReturnSequentialAudio()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var partial = Source([1, 2, 3]);
        partial.StatusCode = HttpStatusCode.PartialContent;
        partial.Content.Headers.ContentRange = new(10, 12, 100);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "one", It.IsAny<ProviderAudioQuality>(), "bytes=10-", false))
            .ReturnsAsync(new ProtocolProviderStream(partial, Lease(), "fixture", "one"));
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "one", It.IsAny<ProviderAudioQuality>(), null, false))
            .ReturnsAsync(new ProtocolProviderStream(Source(Wave(2)), Lease(), "fixture", "one"));
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal", null, "conversion", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var http = new DefaultHttpContext();
        http.Request.Headers.Range = "bytes=10-";
        var result = await new ProviderStreamOpener(gateway.Object, new()).OpenAsync(http, context, "fixture", "one", ProviderAudioQuality.Any, Request(ProtocolKind.Jellyfin));
        using var stream = Assert.IsType<FileStreamResult>(result).FileStream;
        Assert.False(((FileStreamResult)result).EnableRangeProcessing);
        await stream.CopyToAsync(Stream.Null);
        Assert.Equal(200, http.Response.StatusCode);
        Assert.Null(http.Response.ContentLength);
        Assert.Equal(0, http.Response.Headers.ContentRange.Count);
        gateway.VerifyAll();
    }

    private static ProviderAudioDeliveryRequest Request(ProtocolKind protocol) => protocol == ProtocolKind.Jellyfin
        ? ProviderAudioDeliveryRequests.Jellyfin(Query(("MaxStreamingBitrate", "128000")))
        : ProviderAudioDeliveryRequests.Subsonic(new("GET", null, null,
            [new("format", "mp3", SubsonicParameterSource.Query), new("maxBitRate", "128", SubsonicParameterSource.Query)]));
    private static QueryCollection Query(params (string Key, string Value)[] values) => new(values.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));
    private static HttpResponseMessage Source(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static ProviderStreamLease Lease() => new("fixture", new("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1), true, true, WaveMedia, ProviderStreamRetryBehavior.DoNotRetry);
    private static byte[] Wave(int seconds)
    {
        var bytes = new byte[44 + 44100 * 4 * seconds];
        "RIFF"u8.CopyTo(bytes); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 44100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 176400);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), bytes.Length - 44);
        for (var index = 0; index < (bytes.Length - 44) / 2; index++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + index * 2), (short)(Math.Sin(index / 2 * Math.PI * 880 / 44100) * 12000));
        return bytes;
    }

    private sealed class TailStream(byte[] bytes, int boundary) : Stream
    {
        private int position;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released => release.Task.IsCompleted;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (position >= boundary) await release.Task.WaitAsync(token);
            var count = Math.Min(buffer.Length, Math.Min(bytes.Length - position, position < boundary ? boundary - position : int.MaxValue));
            bytes.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { if (disposing) release.TrySetCanceled(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
