using System.Buffers.Binary;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    [MediaToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DerivedDurationHintsPreserveCompleteAudioAndVerifiedTimelineAcrossRestart(bool guided)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "audio");
        Directory.CreateDirectory(root);
        var wave = Path.Combine(root, "input.wav");
        var path = Path.Combine(root, "input.flac");
        await File.WriteAllBytesAsync(wave, PlaybackAudioFixtures.Wave());
        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg",
            ["-v", "error", "-nostdin", "-threads", "1", "-i", wave, "-c:a", "flac", "-threads", "1", path]);
        var bytes = await File.ReadAllBytesAsync(path);
        var packed = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18), packed & ~((1UL << 36) - 1));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build();
        var settings = Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache });
        var mappings = new EfDownloadedSongMappingStore(new RetainedOriginalCacheFixture.Factory(database.Options));
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        using var noNetwork = new HttpClient(new DurationNoNetwork());
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(noNetwork);
        var library = new LocalLibraryService(configuration, clients.Object, settings, mappings, NullLogger<LocalLibraryService>.Instance);
        ManagedTrackCacheService CreateCache() => new(configuration, settings, library, NullLogger<ManagedTrackCacheService>.Instance,
            mappings: mappings, audioVerifier: new ProviderAudioVerifier());
        var scope = new DownloadedSongMappingScope(null, ProviderAudioQuality.Any);
        var track = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "derived-duration");
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener",
            new(Guid.NewGuid(), "jellyfin", "fixture", "listener", "Listener", false), "derived", DateTimeOffset.UtcNow.AddMinutes(1), default);
        Task<Song?> Metadata() => Task.FromResult<Song?>(new() { Title = "Derived", Artist = "Fixture", PreciseDuration = TimeSpan.FromSeconds(8.125) });
        byte[] delivered;
        await using (var cache = CreateCache())
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            var stream = new ProtocolProviderStream(response, new("source", new Uri("https://media.example.test/audio"), protocol.Deadline,
                false, false, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry), track.ProviderId, track.Value);
            if (guided) StreamDurationGuidance.Attach(stream, Metadata);
            await cache.WrapAsync(stream, protocol, track.ProviderId, track.Value, false, Metadata, default);
            delivered = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(guided, stream.FileFacts.DurationIsDerived);
            Assert.Equal(guided ? 8.125 : null, AudioMediaFacts.ReadPrefix(delivered, delivered.Length)!.DurationSeconds);
            Assert.Equal(8, stream.FileFacts.Measured!.DurationSeconds);
        }
        await using (var restarted = CreateCache())
        {
            var replay = (await restarted.TryOpenAsync(track, scope, default))!;
            using (replay.Response)
            {
                Assert.Equal(8, replay.FileFacts.Measured!.DurationSeconds);
                Assert.Equal(delivered, await replay.Response.Content.ReadAsByteArrayAsync());
            }
            var seek = (await restarted.TryOpenAsync(track, scope, default))!;
            using (seek.Response)
            {
                await ProviderAudioTimeSeek.ApplyAsync(seek.Response, new DefaultHttpContext().Response, TimeSpan.FromSeconds(4).Ticks,
                    null, default, seek.Lease.Media, seek.FileFacts.Measured!.DurationSeconds);
                var output = Path.Combine(root, "offset.flac");
                await File.WriteAllBytesAsync(output, await seek.Response.Content.ReadAsByteArrayAsync());
                Assert.InRange((await AudioMediaFacts.ReadFileAsync(output, default))!.DurationSeconds!.Value, 3.9999, 4.0001);
            }
        }
        clients.Verify(item => item.CreateClient(It.IsAny<string>()), Times.Once);
        clients.VerifyNoOtherCalls();
    }

    private sealed class DurationNoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No provider calls are allowed.");
    }
}
