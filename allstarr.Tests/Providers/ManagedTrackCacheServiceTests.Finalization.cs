using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Downloads;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Moq;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedAppleCacheFinalizesPrivatelyAndKeepsProgressiveReadersUnchanged(bool fails)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalized = "completed-cache-with-metadata"u8.ToArray();
        async Task Finalize(string source, string target, ProviderMediaFormat media, CancellationToken token)
        {
            Assert.Equal(Enumerable.Range(0, 16).Select(value => (byte)value), await File.ReadAllBytesAsync(source, token));
            await File.WriteAllBytesAsync(target, finalized, token);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            if (fails) throw new IOException("Fixture finalization failed.");
        }
        var clock = new ManualTimeProvider();
        await using var fixture = new ProgressiveFixture(knownLength: false, provider: "apple-musickit",
            timeProvider: clock, finalizer: Finalize);
        await fixture.StartAsync();
        fixture.Source.Release.TrySetResult();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(fixture.PublishedPath);
        using var coalesced = (await fixture.OpenAsync())!.Response;
        var replay = coalesced.Content.ReadAsByteArrayAsync();
        release.TrySetResult();
        Assert.Equal(fixture.Bytes, await fixture.Response.Content.ReadAsByteArrayAsync());
        Assert.Equal(fixture.Bytes, await replay);
        Assert.Equal(fails ? fixture.Bytes : finalized, await File.ReadAllBytesAsync(fixture.PublishedPath!));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.partial", SearchOption.AllDirectories));
        coalesced.Dispose();
        fixture.Response.Dispose();
        using (var cached = (await fixture.OpenAsync())!.Response)
        {
            Assert.Equal(fails ? fixture.Bytes : finalized, await cached.Content.ReadAsByteArrayAsync());
            Assert.Equal(fails ? fixture.Bytes.Length : finalized.Length, cached.Content.Headers.ContentLength);
        }
        clock.Advance(TimeSpan.FromHours(2));
        await fixture.Service.CleanupExpiredAudioAsync(TimeSpan.FromHours(1));
        Assert.False(File.Exists(fixture.PublishedPath));
    }

    [MediaToolsTheory]
    [InlineData("alac-artwork.flac")]
    [InlineData("aac-artwork.m4a")]
    [InlineData("alac-24-48000-original.m4a")]
    public async Task CompletedAppleCachePublishesFinalizedFixtureWithMeasuredDuration(string file)
    {
        var sample = AppleProgressiveFixtures.Sample(file);
        var root = CreateRoot();
        try
        {
            string? published = null;
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(
                    It.IsAny<DownloadedSongMappingScope>(), "apple-musickit", "track-1"))
                .ReturnsAsync(() => published);
            local.Setup(item => item.RegisterDownloadedSongAsync(
                    It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((_, _, path) => published = path)
                .Returns(Task.CompletedTask);
            await using var service = CreateService(root, "Cache", local.Object, audioVerifier: new ProviderAudioVerifier());
            var bytes = AppleProgressiveFixtures.Bytes(file);
            using var source = new AppleProgressiveFixtures.PendingTail(bytes);
            using var response = AppleProgressiveFixtures.Response(source, sample.Mime);
            var stream = ProviderStream(response) with
            {
                ServingProviderId = "apple-musickit",
                Lease = new("fixture", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                    false, false, sample.Media, ProviderStreamRetryBehavior.DoNotRetry)
            };
            await service.WrapAsync(stream, Context(), "apple-musickit", "track-1", false,
                () => Task.FromResult<Song?>(null), default);
            source.Release.TrySetResult();
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.NotNull(published);
            var facts = await AudioMediaFacts.ReadFileAsync(published, default);
            var evidence = await ExtensionDownloadDecryptionTests.RunAsync("ffprobe", ["-v", "error", "-threads", "1",
                "-select_streams", "a:0", "-show_entries", "format=duration:packet=pts,duration:frame=nb_samples,pts",
                "-of", "json", published]);
            Assert.True(Math.Abs(facts!.DurationSeconds!.Value - 3) < 0.05 && facts.DurationSeconds >= 2.99, evidence);
            Assert.NotEqual(bytes, await File.ReadAllBytesAsync(published));
        }
        finally { DeleteRoot(root); }
    }
}
