using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderStreamOpenerTests
{
    [MediaToolsFact]
    public async Task TimeOffsets_ReplayCompletedCacheForBothProtocolsWithoutFetchingAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-seek-test", Guid.NewGuid().ToString("N"));
        var bytes = SeekWave();
        string? published = null;
        var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
        local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "apple-download", "6764050144"))
            .ReturnsAsync(() => published);
        local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
            .Callback<DownloadedSongMappingScope, Song, string>((_, _, path) => published = path).Returns(Task.CompletedTask);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build();
        await using var cache = new ManagedTrackCacheService(configuration, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            local.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagedTrackCacheService>.Instance, audioVerifier: FixtureAudioVerifier.Instance);
        var scope = new DownloadedSongMappingScope(null, ProviderAudioQuality.Any);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "listener",
            new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "listener", "Listener", false), "seek",
            DateTimeOffset.UtcNow.AddMinutes(3), default, new("Musiver", "device", "Phone"));
        var fetches = 0;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "6764050144"))
            .ReturnsAsync(new Song { Title = "Housewife", Artist = "Tate McRae", Duration = 40 });
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "6764050144", ProviderAudioQuality.Any, null, false))
            .Returns(async () =>
            {
                var cached = await cache.TryOpenAsync(new("apple-download", ProviderResourceKind.Track, "6764050144"), scope, default);
                if (cached != null) return cached;
                fetches++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new("audio/wav");
                return new ProtocolProviderStream(response, new("fixture", new Uri("https://media.example.test/audio"),
                    DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/wav", "wav", "pcm_s16le", sampleRate: 8000, channels: 1),
                    ProviderStreamRetryBehavior.DoNotRetry), "apple-download", "6764050144");
            });
        try
        {
            var opener = new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter(), cache);
            var start = await opener.OpenAsync(new DefaultHttpContext(), context, "apple-download", "6764050144", ProviderAudioQuality.Any);
            await using (var input = Assert.IsType<FileStreamResult>(start).FileStream)
            {
                using var output = new MemoryStream();
                await input.CopyToAsync(output);
                Assert.Equal(bytes, output.ToArray());
            }
            Assert.NotNull(published);
            foreach (var protocol in new[] { ProtocolKind.Jellyfin, ProtocolKind.Subsonic })
            {
                var request = protocol == ProtocolKind.Jellyfin
                    ? ProviderAudioDeliveryRequests.Jellyfin(new QueryCollection(new Dictionary<string, StringValues> { ["StartTimeTicks"] = "360000000" }))
                    : ProviderAudioDeliveryRequests.Subsonic(new SubsonicRequestParameters("GET", null, null, [new("timeOffset", "36", SubsonicParameterSource.Query)]));
                var http = new DefaultHttpContext();
                var seekContext = new ProtocolExecutionContext(protocol, "backend", "listener", null, "seek", DateTimeOffset.UtcNow.AddMinutes(1), default);
                var result = await opener.OpenAsync(http, seekContext, "apple-download", "6764050144", ProviderAudioQuality.Any, request);
                await using var input = Assert.IsType<FileStreamResult>(result).FileStream;
                using var output = new MemoryStream();
                await input.CopyToAsync(output);
                var path = Path.Combine(root, "offset.wav");
                await File.WriteAllBytesAsync(path, output.ToArray());
                var facts = await AudioMediaFacts.ReadFileAsync(path, default);
                Assert.InRange(facts!.DurationSeconds!.Value, 3.9, 4.1);
                Assert.Equal(output.Length, http.Response.ContentLength);
                Assert.Equal("36", http.Response.Headers["X-Allstarr-Start-Seconds"]);
                Assert.Equal("40", http.Response.Headers["X-Content-Duration"]);
                var samples = await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-i", path, "-f", "md5", "-"]);
                var expected = Path.Combine(root, "expected.wav");
                await File.WriteAllBytesAsync(expected, SeekWave(4, 36));
                Assert.Equal(await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-i", expected, "-f", "md5", "-"]), samples);
            }
            Assert.Equal(1, fetches);
        }
        finally
        {
            await cache.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [MediaToolsFact]
    public async Task TimeOffsets_ColdSourceStreamsTheRemainingAudioAndRejectsPartialInput()
    {
        foreach (var truncated in new[] { false, true })
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SeekWave()) };
            response.Content.Headers.ContentType = new("audio/wav");
            if (truncated) response.Content.Headers.ContentLength = SeekWave().Length + 1;
            var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
            gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Any, null, false))
                .ReturnsAsync(new ProtocolProviderStream(response, new("fixture", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                    false, false, new("audio/wav", "wav", "pcm_s16le"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42"));
            var opening = new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
                .OpenAsync(new DefaultHttpContext(), _context, "deezer", "42", ProviderAudioQuality.Any, new() { StartTimeTicks = 360000000 });
            async Task<byte[]> ReadRemainingAsync()
            {
                var result = await opening;
                await using var stream = Assert.IsType<FileStreamResult>(result).FileStream;
                using var output = new MemoryStream();
                await stream.CopyToAsync(output);
                return output.ToArray();
            }
            if (truncated) await Assert.ThrowsAsync<IOException>(ReadRemainingAsync);
            else Assert.InRange((await ReadRemainingAsync()).Length, 64001, 64999);
        }
    }

    [MediaToolsFact]
    public async Task TimeOffsets_KeepCodecAndRemainingDurationAcrossProviderContainers()
    {
        var root = Directory.CreateTempSubdirectory("allstarr-seek-codecs-").FullName;
        try
        {
            var wave = Path.Combine(root, "source.wav");
            await File.WriteAllBytesAsync(wave, SeekWave(sampleRate: 44100));
            foreach (var (extension, codec) in new[] { ("flac", "flac"), ("mp3", "libmp3lame"), ("m4a", "aac"), ("m4a", "alac"), ("aac", "aac"), ("ogg", "libopus") })
            {
                var input = Path.Combine(root, codec + "." + extension);
                await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-y", "-i", wave, "-threads", "1", "-c:a", codec, input]);
                var before = await AudioMediaFacts.ReadFileAsync(input, default);
                using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(input)) };
                response.Content.Headers.ContentType = new(before!.Media.MimeType);
                var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
                gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Any, null, false))
                    .ReturnsAsync(new ProtocolProviderStream(response, new("fixture", new Uri("https://cache.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                        true, true, before.Media, ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42", IsCached: true));
                var http = new DefaultHttpContext();
                var result = await new ProviderStreamOpener(gateway.Object, new ProtocolStreamingResponseAdapter())
                    .OpenAsync(http, _context, "deezer", "42", ProviderAudioQuality.Any, new() { StartTimeTicks = 360000000 });
                await using var stream = Assert.IsType<FileStreamResult>(result).FileStream;
                var output = Path.Combine(root, "remaining." + extension);
                await using (var file = File.Create(output)) await stream.CopyToAsync(file);
                if (extension == "aac")
                    Assert.InRange(double.Parse(http.Response.Headers["X-Content-Duration"].ToString(), System.Globalization.CultureInfo.InvariantCulture), 40, 40.1);
                var after = await AudioMediaFacts.ReadFileAsync(output, default);
                Assert.Equal(before.Media.Codec, after!.Media.Codec);
                Assert.Equal(before.Media.SampleRate, after.Media.SampleRate);
                Assert.Equal(before.Media.Channels, after.Media.Channels);
                Assert.InRange(after.DurationSeconds!.Value, 3.75, 4.35);
                if (codec == "flac")
                {
                    var expected = Path.Combine(root, "expected.wav");
                    await File.WriteAllBytesAsync(expected, SeekWave(4, 36, 44100));
                    Assert.Equal(await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-i", expected, "-f", "md5", "-"]),
                        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg", ["-v", "error", "-i", output, "-f", "md5", "-"]));
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TimeOffsets_UseTheSharedConversionCapacityAndReleaseAfterFailure()
    {
        var gate = new MediaConversionGate(new MediaCapacityLimits(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [MediaCapacityLimits.ConversionsKey] = "1" }).Build()));
        using var occupied = await gate.EnterAsync(default);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SeekWave()) };
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", ProviderAudioQuality.Any, null, false))
            .ReturnsAsync(new ProtocolProviderStream(response, Lease(), "deezer", "42", IsCached: true));
        var http = new DefaultHttpContext();
        await Assert.ThrowsAsync<LocalCapacityException>(() => new ProviderStreamOpener(gateway.Object,
            new ProtocolStreamingResponseAdapter(), conversions: gate).OpenAsync(http, _context, "deezer", "42", ProviderAudioQuality.Any,
                new() { StartTimeTicks = 360000000 }));
        Assert.Equal("5", http.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task RejectedAudio_LogsReasonAndSanitizedClientAndSettings()
    {
        var logger = new SeekLogger();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        http.Request.QueryString = new("?api_key=never-log-this&AudioCodec=flac");
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal", null, "rejected",
            DateTimeOffset.UtcNow.AddMinutes(1), default, new("Musiver api_key=private-token", "private-device-id", "Phone token=private-device-token"));
        using var trace = new System.Diagnostics.Activity("stream").Start();
        await Assert.ThrowsAsync<ProviderAudioProfileException>(() => new ProviderStreamOpener(gateway.Object,
            new ProtocolStreamingResponseAdapter(), logger: logger).OpenAsync(http, context, "deezer", "42", ProviderAudioQuality.Any,
                new() { EnableDirectPlay = false, EnableDirectStream = false, EnableTranscoding = false, Formats = [new(["flac"], ["flac"], ["http"])], MaxBitrate = 128000, StartTimeTicks = 360000000 }));
        var log = Assert.Single(logger.Messages);
        Assert.Contains("All audio delivery methods are disabled", log);
        Assert.Contains("Musiver", log);
        Assert.Contains("Phone", log);
        Assert.Contains("flac", log);
        Assert.Contains("128000", log);
        Assert.Contains("360000000", log);
        Assert.DoesNotContain("private-", log);
        Assert.DoesNotContain("never-log-this", log);
        Assert.Equal("All audio delivery methods are disabled.", trace.GetTagItem("audio.rejection_reason"));
        gateway.VerifyNoOtherCalls();
    }

    internal static byte[] SeekWave(int seconds = 40, int start = 0, int sampleRate = 8000)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8); writer.Write(36 + seconds * sampleRate * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(seconds * sampleRate * 2);
        for (var sample = 0; sample < seconds * sampleRate; sample++) writer.Write((short)(((sample + start * sampleRate) * 17) % 30000 - 15000));
        return output.ToArray();
    }

    private sealed class SeekLogger : ILogger<ProviderStreamOpener>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
    }
}
