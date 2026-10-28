using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Health;
using allstarr.Core.Downloads;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin, true, "aac")]
    [InlineData(ProtocolKind.Subsonic, true, "mp3")]
    [InlineData(ProtocolKind.Jellyfin, false, "aac")]
    [InlineData(ProtocolKind.Subsonic, false, "mp3")]
    public async Task MovieConversionAndOriginalVerificationShareOneSlot(ProtocolKind dialect, bool streamable, string codec)
    {
        var root = Directory.CreateTempSubdirectory("allstarr-movie-conversion-").FullName;
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tailCapacityChecks = 0;
        var fullOpens = 0;
        var local = new Mock<ILocalLibraryService>();
        local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<allstarr.Models.Domain.Song>(), It.IsAny<string>()))
            .Callback<DownloadedSongMappingScope, allstarr.Models.Domain.Song, string>((_, _, path) => published.TrySetResult(path)).Returns(Task.CompletedTask);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = root, [MediaCapacityLimits.ConversionsKey] = "1" }).Build();
        var gate = new MediaConversionGate(new MediaCapacityLimits(config));
        var verifier = new ProviderAudioVerifier(gate);
        await using var cache = new ManagedTrackCacheService(config, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            local.Object, NullLogger<ManagedTrackCacheService>.Instance, audioVerifier: verifier);
        try
        {
            var wave = PlaybackAudioFixtures.Wave();
            System.Security.Cryptography.RandomNumberGenerator.Fill(wave.AsSpan(44));
            var input = Path.Combine(root, "input.wav");
            var movie = Path.Combine(root, "input.m4a");
            await File.WriteAllBytesAsync(input, wave);
            var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-v", "error", "-nostdin", "-threads", "1", "-i", input, "-c:a", "alac", "-threads", "1" }) start.ArgumentList.Add(argument);
            if (streamable)
            {
                start.ArgumentList.Add("-movflags");
                start.ArgumentList.Add("+faststart");
            }
            start.ArgumentList.Add(movie);
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                await process.WaitForExitAsync();
                await errors;
                Assert.Equal(0, process.ExitCode);
            }
            var bytes = await File.ReadAllBytesAsync(movie);
            Assert.True(bytes.Length > 256 * 1024);
            var first = PipelineSource("deezer", (request, _) =>
            {
                if (request.Headers.Range == null)
                {
                    Interlocked.Increment(ref fullOpens);
                    var full = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new GateBeforeTailStream(bytes, gate,
                            () => Interlocked.Increment(ref tailCapacityChecks), streamable))
                    };
                    full.Content.Headers.ContentLength = bytes.Length;
                    full.Headers.ETag = new("\"original\"");
                    return Task.FromResult(full);
                }
                var range = request.Headers.Range.Ranges.Single();
                var from = range.From ?? 0;
                var to = Math.Min(range.To ?? bytes.Length - 1, bytes.Length - 1);
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(bytes[(int)from..((int)to + 1)]) };
                partial.Content.Headers.ContentRange = new(from, to, bytes.Length);
                partial.Headers.ETag = new("\"original\"");
                return Task.FromResult(partial);
            }, new("audio/mp4", "mp4", "alac"));
            var second = PipelineSource("qobuz", (_, _) => throw new InvalidOperationException("The source is playable."));
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, audioVerifier: verifier);
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            http.Request.Headers.Range = "bytes=0-1";
            var delivery = new ProviderAudioDeliveryRequest { MaxBitrate = 128000, PreferredCodec = codec };
            var opener = new ProviderStreamOpener(gateway, new(), cache, conversions: gate);
            var result = await opener.OpenAsync(http, PipelineContext(dialect), "deezer", "source-track", delivery.Quality, delivery)
                .WaitAsync(TimeSpan.FromSeconds(30));
            using var stream = Assert.IsType<Microsoft.AspNetCore.Mvc.FileStreamResult>(result).FileStream;
            using var encoded = new MemoryStream();
            await stream.CopyToAsync(encoded).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(codec, AudioMediaFacts.ReadPrefix(encoded.ToArray(), encoded.Length)!.Media.Codec);
            Assert.Equal(200, http.Response.StatusCode);
            Assert.Equal(0, http.Response.Headers.ContentRange.Count);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(await published.Task.WaitAsync(TimeSpan.FromSeconds(30))));
            Assert.Equal(1, fullOpens);
            Assert.Equal(1, tailCapacityChecks);
            using var capacity = await gate.EnterAsync(default);
        }
        finally { await cache.DisposeAsync(); Directory.Delete(root, true); }
    }

    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin, "aac")]
    [InlineData(ProtocolKind.Subsonic, "mp3")]
    public async Task InitialRangeAndCappedPlaybackOpenTheVerifiedSharedSourceBeforeTakingTheSingleConversionSlot(
        ProtocolKind dialect, string codec)
    {
        var root = Directory.CreateTempSubdirectory("allstarr-range-conversion-").FullName;
        var bytes = PlaybackAudioFixtures.Wave();
        var fullOpens = 0;
        var prefixOpens = 0;
        var tailCapacityChecks = 0;
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new Mock<ILocalLibraryService>();
        local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<allstarr.Models.Domain.Song>(), It.IsAny<string>()))
            .Callback<DownloadedSongMappingScope, allstarr.Models.Domain.Song, string>((_, _, path) => published.TrySetResult(path)).Returns(Task.CompletedTask);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = root, [MediaCapacityLimits.ConversionsKey] = "1" }).Build();
        var gate = new MediaConversionGate(new MediaCapacityLimits(config));
        var verifier = new ProviderAudioVerifier(gate);
        await using var cache = new ManagedTrackCacheService(config, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            local.Object, NullLogger<ManagedTrackCacheService>.Instance, audioVerifier: verifier);
        try
        {
            var first = PipelineSource("deezer", (request, _) =>
            {
                HttpResponseMessage response;
                if (request.Headers.Range == null)
                {
                    Interlocked.Increment(ref fullOpens);
                    response = new(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new GateBeforeTailStream(bytes, gate,
                            () => Interlocked.Increment(ref tailCapacityChecks)))
                    };
                    response.Content.Headers.ContentLength = bytes.Length;
                }
                else
                {
                    var range = request.Headers.Range.Ranges.Single();
                    var from = range.From ?? 0;
                    var to = Math.Min(range.To ?? bytes.Length - 1, bytes.Length - 1);
                    if (to > 1) Interlocked.Increment(ref prefixOpens);
                    response = new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[(int)from..((int)to + 1)]) };
                    response.Content.Headers.ContentRange = new(from, to, bytes.Length);
                }
                response.Headers.ETag = new("\"original\"");
                return Task.FromResult(response);
            });
            var second = PipelineSource("qobuz", (_, _) => throw new InvalidOperationException("The source is playable."));
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, audioVerifier: verifier);
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            http.Request.Headers.Range = "bytes=0-1";
            var delivery = new ProviderAudioDeliveryRequest { MaxBitrate = 128000, PreferredCodec = codec };
            var opener = new ProviderStreamOpener(gateway, new(), cache, conversions: gate);

            var result = await opener.OpenAsync(http, PipelineContext(dialect), "deezer", "source-track", delivery.Quality, delivery)
                .WaitAsync(TimeSpan.FromSeconds(20));
            using var stream = Assert.IsType<Microsoft.AspNetCore.Mvc.FileStreamResult>(result).FileStream;
            using var encoded = new MemoryStream();
            await stream.CopyToAsync(encoded).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(codec, AudioMediaFacts.ReadPrefix(encoded.ToArray(), encoded.Length)!.Media.Codec);
            Assert.Equal(200, http.Response.StatusCode);
            Assert.Equal(0, http.Response.Headers.ContentRange.Count);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(await published.Task.WaitAsync(TimeSpan.FromSeconds(20))));
            Assert.Equal(1, fullOpens);
            Assert.True(prefixOpens > 0);
            Assert.Equal(1, tailCapacityChecks);
            using var capacity = await gate.EnterAsync(default);
        }
        finally { await cache.DisposeAsync(); Directory.Delete(root, true); }
    }

    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task KnownRecordingDurationRejectsAnIncorrectAlternativeBeforeDelivery(ProtocolKind dialect)
    {
        var recording = Guid.NewGuid().ToString("D");
        var identities = new Mock<allstarr.Core.Matching.ITrackIdentityService>();
        identities.Setup(item => item.GetRecordingAvailabilityAsync(It.IsAny<ProviderExecutionContext>(),
            "ext-allstarr-song-" + recording, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderRecordingAvailabilityRequest(recording, "Fixture", ["Artist"], null, 8000,
                new Dictionary<string, string>()));
        var incorrect = PipelineSource("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(PlaybackAudioFixtures.Wave(16)) }));
        var bytes = PlaybackAudioFixtures.Wave();
        var correct = PipelineSource("qobuz", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var (gateway, _) = FailoverGateway(incorrect, correct, identities: identities.Object, audioVerifier: new ProviderAudioVerifier());
        var opened = (await gateway.OpenStreamAsync(PipelineContext(dialect), "allstarr", recording, ProviderAudioQuality.Any, null))!;
        using var response = opened.Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task CompletedDownloadSurvivesReaderDisposalWhileWaitingForFinalVerification(ProtocolKind dialect)
    {
        var root = Directory.CreateTempSubdirectory("allstarr-pending-verification-").FullName;
        var bytes = PlaybackAudioFixtures.Wave();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var verifier = new PendingCompletionVerifier();
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new Mock<ILocalLibraryService>();
        local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<allstarr.Models.Domain.Song>(), It.IsAny<string>()))
            .Callback<DownloadedSongMappingScope, allstarr.Models.Domain.Song, string>((_, _, path) => published.TrySetResult(path)).Returns(Task.CompletedTask);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build();
        await using var cache = new ManagedTrackCacheService(config, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            local.Object, NullLogger<ManagedTrackCacheService>.Instance, timeProvider: clock, audioVerifier: verifier);
        try
        {
            var first = PipelineSource("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            var second = PipelineSource("qobuz", (_, _) => throw new InvalidOperationException("The original is playable."));
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, audioVerifier: new ProviderAudioVerifier());
            using var response = (await gateway.OpenStreamAsync(PipelineContext(dialect), "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
            var body = await response.Content.ReadAsStreamAsync();
            await body.ReadExactlyAsync(new byte[bytes.Length]);
            await verifier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            response.Dispose();
            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.False(verifier.Token.IsCancellationRequested);
            Assert.False(published.Task.IsCompleted);
            verifier.Release.TrySetResult();
            Assert.Equal(bytes, await File.ReadAllBytesAsync(await published.Task.WaitAsync(TimeSpan.FromSeconds(10))));
        }
        finally { verifier.Release.TrySetResult(); await cache.DisposeAsync(); Directory.Delete(root, true); }
    }

    private sealed class PendingCompletionVerifier : IProviderAudioVerifier
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Task<ProviderOutcome<TrackMediaFacts>> VerifyPrefixAsync(HttpResponseMessage response, ProviderMediaFormat expected,
            double? recordingSeconds, CancellationToken token, Func<long, long?, CancellationToken, Task<HttpResponseMessage>>? openRange = null) =>
            new ProviderAudioVerifier().VerifyPrefixAsync(response, expected, recordingSeconds, token, openRange);
        public async Task<TrackMediaFacts> VerifyFileAsync(string path, ProviderMediaFormat expected, double? recordingSeconds, CancellationToken token, bool derivedDuration = false)
        {
            Token = token;
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return await new ProviderAudioVerifier().VerifyFileAsync(path, expected, recordingSeconds, token);
        }
    }

    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task ProgressiveConversionAndCacheVerificationShareOneSlotAndPublishOnlyTheOriginal(ProtocolKind dialect)
    {
        var root = Directory.CreateTempSubdirectory("allstarr-converted-cache-").FullName;
        var bytes = PlaybackAudioFixtures.Wave();
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new Mock<ILocalLibraryService>();
        local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<allstarr.Models.Domain.Song>(), It.IsAny<string>()))
            .Callback<DownloadedSongMappingScope, allstarr.Models.Domain.Song, string>((_, _, path) => published.TrySetResult(path)).Returns(Task.CompletedTask);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = root, [MediaCapacityLimits.ConversionsKey] = "1" }).Build();
        var gate = new MediaConversionGate(new MediaCapacityLimits(config));
        var verifier = new ProviderAudioVerifier(gate);
        await using var cache = new ManagedTrackCacheService(config, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            local.Object, NullLogger<ManagedTrackCacheService>.Instance, audioVerifier: verifier);
        try
        {
            var first = PipelineSource("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            var second = PipelineSource("qobuz", (_, _) => throw new InvalidOperationException("The original is playable."));
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, audioVerifier: verifier);
            var context = PipelineContext(dialect) with { AudioDelivery = new() { MaxBitrate = 128000, PreferredCodec = "aac" } };
            var opened = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!;
            using var response = opened.Response;
            var conversion = Assert.IsType<ProviderAudioConversion>(ProviderAudioTranscoder.Plan(context.AudioDelivery!, opened.Lease.Media));
            await ProviderAudioTranscoder.ApplyAsync(response, new Microsoft.AspNetCore.Http.DefaultHttpContext().Response, conversion, gate, default);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var path = await published.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            using var capacity = await gate.EnterAsync(default);
        }
        finally { await cache.DisposeAsync(); Directory.Delete(root, true); }
    }

    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task VerifiedStartRejectsBadAudioOrAdvertisedCodecBeforeSelectingTheNextSource(ProtocolKind dialect, bool wrongCodec)
    {
        var bytes = PlaybackAudioFixtures.Wave();
        var rejected = new ObservedContent(wrongCodec ? bytes : new byte[262144]);
        var first = PipelineSource("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = rejected }),
            wrongCodec ? new("audio/flac", "flac", "flac") : PlaybackAudioFixtures.WaveMedia);
        var second = PipelineSource("qobuz", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var (gateway, _) = FailoverGateway(first, second, audioVerifier: new ProviderAudioVerifier());
        var opened = (await gateway.OpenStreamAsync(PipelineContext(dialect), "deezer", "source-track", ProviderAudioQuality.Any, null))!;
        using var response = opened.Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.True(rejected.Disposed);
        Assert.Equal("pcm_s16le", opened.Lease.Media.Codec);
    }

    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin, 0)]
    [InlineData(ProtocolKind.Subsonic, 0)]
    [InlineData(ProtocolKind.Jellyfin, 2)]
    [InlineData(ProtocolKind.Subsonic, 2)]
    public async Task InterruptedOriginalRetriesTheSameByteSourceAndReturnsIdenticalAudio(ProtocolKind dialect, int transientResumes)
    {
        var bytes = PlaybackAudioFixtures.Wave();
        var attempts = 0;
        var first = PipelineSource("deezer", (request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (request.Headers.Range == null)
            {
                response.Content = new StreamContent(new PipelineInterrupted(bytes, 300000));
                response.Content.Headers.ContentLength = bytes.Length;
            }
            else
            {
                Assert.Equal("bytes=300000-", request.Headers.Range.ToString());
                Assert.Equal("\"original\"", request.Headers.IfRange!.ToString());
                response.StatusCode = ++attempts <= transientResumes ? HttpStatusCode.BadGateway : HttpStatusCode.PartialContent;
                response.Content = new ByteArrayContent(attempts <= transientResumes ? [] : bytes[300000..]);
                if (response.StatusCode == HttpStatusCode.PartialContent) response.Content.Headers.ContentRange = new(300000, bytes.Length - 1, bytes.Length);
            }
            response.Headers.ETag = new("\"original\"");
            return Task.FromResult(response);
        });
        var second = PipelineSource("qobuz", (_, _) => throw new InvalidOperationException("Must resume the same bytes."));
        var (gateway, _) = FailoverGateway(first, second, audioVerifier: new ProviderAudioVerifier());
        using var response = (await gateway.OpenStreamAsync(PipelineContext(dialect), "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(transientResumes + 1, attempts);
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [MediaToolsTheory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task PartialOriginalIsNeverPublishedAndTheNextRequestUsesAnotherSource(ProtocolKind dialect, bool declaredLength)
    {
        var root = Directory.CreateTempSubdirectory("allstarr-pipeline-").FullName;
        var bytes = PlaybackAudioFixtures.Wave();
        string? published = null;
        var library = new Mock<ILocalLibraryService>();
        library.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => published);
        library.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<allstarr.Models.Domain.Song>(), It.IsAny<string>()))
            .Callback<DownloadedSongMappingScope, allstarr.Models.Domain.Song, string>((_, _, path) => published = path).Returns(Task.CompletedTask);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build();
        await using var cache = new ManagedTrackCacheService(config, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            library.Object, NullLogger<ManagedTrackCacheService>.Instance);
        try
        {
            var first = PipelineSource("deezer", (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableAudio(bytes[..300000])) };
                if (declaredLength) response.Content.Headers.ContentLength = bytes.Length;
                return Task.FromResult(response);
            });
            var second = PipelineSource("qobuz", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, outcomes: new ProviderRuntimeHealth(), audioVerifier: new ProviderAudioVerifier());
            var context = PipelineContext(dialect);
            using (var failed = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response)
                await Assert.ThrowsAsync<HttpRequestException>(() => failed.Content.ReadAsByteArrayAsync());
            Assert.Null(published);
            using var retry = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
            Assert.Equal("qobuz", retry.Headers.GetValues("X-Allstarr-Provider").Single());
            Assert.Equal(bytes, await retry.Content.ReadAsByteArrayAsync());
            first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        }
        finally { await cache.DisposeAsync(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task CappedPlaybackAndInspectionReuseAnAuthorizedHigherQualityOriginalWithoutOpeningProviders(ProtocolKind dialect, bool inspect)
    {
        var root = Directory.CreateTempSubdirectory("allstarr-capped-cache-").FullName;
        var path = Path.Combine(root, "original.wav");
        var bytes = PlaybackAudioFixtures.Wave();
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "source-track"))
                .ReturnsAsync((DownloadedSongMappingScope scope, string _, string _) => scope.AudioQuality == ProviderAudioQuality.HighResolution ? path : null);
            await using var cache = new ManagedTrackCacheService(new ConfigurationBuilder().Build(),
                Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }), local.Object, NullLogger<ManagedTrackCacheService>.Instance);
            var first = PipelineSource("deezer", (_, _) => throw new InvalidOperationException("Must use the existing original."));
            var second = PipelineSource("qobuz", (_, _) => throw new InvalidOperationException("Must use the existing original."));
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, audioVerifier: new ProviderAudioVerifier());
            var context = PipelineContext(dialect) with
            { AudioDelivery = new() { MaxBitrate = 128000, PreferredCodec = dialect == ProtocolKind.Jellyfin ? "aac" : "mp3" }, IsReadOnly = inspect };
            var opened = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.DataSaver, null, inspect))!;
            using var response = opened.Response;
            Assert.True(opened.IsCached);
            Assert.Equal("pcm_s16le", opened.Lease.Media.Codec);
            Assert.Equal(512000, opened.Lease.Media.Bitrate);
            if (!inspect) Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            foreach (var source in new[] { first, second })
                source.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        }
        finally { Directory.Delete(root, true); }
    }

    private static Mock<IProviderStreamingCapability> PipelineSource(string provider, ProviderStreamResponseFactory open, ProviderMediaFormat? media = null) =>
        Capability(provider, ProviderOutcome<ProviderStreamLease>.Success(new("pipeline", new Uri("https://media.example.test/original"),
            DateTimeOffset.UtcNow.AddMinutes(2), true, true, media ?? PlaybackAudioFixtures.WaveMedia, ProviderStreamRetryBehavior.DoNotRetry, open)));

    private static ProtocolExecutionContext PipelineContext(ProtocolKind dialect)
    {
        var source = ClientContext();
        return new(dialect, source.BackendInstanceId, source.VerifiedBackendPrincipalId,
            source.Principal! with { BackendType = dialect.ToString().ToLowerInvariant() }, source.CorrelationId, source.Deadline, default, source.Client);
    }

    private sealed class PipelineInterrupted(byte[] bytes, int failureAt) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == failureAt) throw new IOException("Interrupted fixture source.");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, failureAt - Position)], cancellationToken);
        }
    }

    private sealed class GateBeforeTailStream(byte[] bytes, MediaConversionGate gate, Action checkedTail, bool checkEncoderGate = true) : MemoryStream(bytes)
    {
        private const int PrefixLength = 256 * 1024;
        private bool tailChecked;
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!tailChecked && Position >= PrefixLength)
            {
                if (checkEncoderGate) await Assert.ThrowsAsync<LocalCapacityException>(async () =>
                {
                    using var unexpectedCapacity = await gate.EnterAsync(cancellationToken);
                });
                tailChecked = true;
                checkedTail();
            }
            if (Position < PrefixLength) buffer = buffer[..(int)Math.Min(buffer.Length, PrefixLength - Position)];
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
