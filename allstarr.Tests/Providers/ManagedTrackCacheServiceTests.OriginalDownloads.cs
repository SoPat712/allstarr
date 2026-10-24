using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalDownloads_KeepIndependentRangesWithoutDurablePublicationOrMetadata(bool knownLength)
    {
        await using var fixture = new ProgressiveFixture(knownLength);
        var records = 0;
        var routed = OriginalProviderStream(fixture);
        routed.FileFacts.RecordAsync = (_, _) => { records++; return Task.CompletedTask; };
        await fixture.Service.WrapAsync(routed, Context() with { IsOriginalDownload = true }, "deezer", "track-1", false,
            () => throw new InvalidOperationException("Original media must not fetch playback metadata."), default);
        using var startup = (await OpenOriginalAsync(fixture))!.Response;
        var initial = await startup.Content.ReadAsStreamAsync();
        Assert.Equal(0, initial.ReadByte());
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        startup.Dispose();
        fixture.Response.Dispose();
        using var one = (await OpenOriginalAsync(fixture))!.Response;
        using var two = (await OpenOriginalAsync(fixture))!.Response;
        var first = ExecuteAsync(one, "bytes=10-11");
        var second = ExecuteAsync(two, "bytes=12-15");
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        fixture.Source.Release.TrySetResult();
        var result = await first.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(206, result.Context.Response.StatusCode);
        Assert.Equal(fixture.Bytes[10..12], result.Bytes);
        Assert.Equal(fixture.Bytes[12..], (await second.WaitAsync(TimeSpan.FromSeconds(8))).Bytes);
        Assert.Equal(0, records);
        Assert.Null(fixture.PublishedPath);
        Assert.Empty(Directory.Exists(fixture.Root) ? Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task OriginalDownloads_AreIsolatedFromPlaybackAccountsRevisionsAndQuality()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.Service.WrapAsync(OriginalProviderStream(fixture), Context() with { IsOriginalDownload = true },
            "deezer", "track-1", false, () => Task.FromResult<Song?>(null), default);
        using var original = (await OpenOriginalAsync(fixture))!.Response;
        Assert.Null(await fixture.OpenAsync());
        Assert.Null(await fixture.Service.TryOpenAsync(fixture.Track, fixture.Scope, default, 5, originalDownload: true));
        Assert.Null(await fixture.Service.TryOpenAsync(fixture.Track, new(Guid.NewGuid(), ProviderAudioQuality.Any),
            default, 4, originalDownload: true));
        Assert.Null(await fixture.Service.TryOpenAsync(fixture.Track, new(fixture.Scope.ProviderAccountId, ProviderAudioQuality.Lossless),
            default, 4, originalDownload: true));
        using var sameScope = (await OpenOriginalAsync(fixture))!.Response;
        Assert.Null(fixture.PublishedPath);
    }

    [Fact]
    public async Task OriginalDownloads_AbsoluteLifetimeStopsAProducerEvenWhileAReaderRemainsOpen()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new ProgressiveFixture(knownLength: true, timeProvider: clock);
        await fixture.Service.WrapAsync(OriginalProviderStream(fixture), Context() with { IsOriginalDownload = true },
            "deezer", "track-1", false, () => Task.FromResult<Song?>(null), default);
        using var response = (await OpenOriginalAsync(fixture))!.Response;
        var reader = await response.Content.ReadAsStreamAsync();
        Assert.Equal(0, reader.ReadByte());
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await clock.WaitForTimersAsync(TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Source.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await OpenOriginalAsync(fixture));
        Assert.Null(fixture.PublishedPath);
    }

    [Fact]
    public async Task OriginalDownloads_CompletedMediaHasAReattachmentWindowAndNeverUsesDurableFallback()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new ProgressiveFixture(knownLength: true, timeProvider: clock);
        fixture.Source.Release.TrySetResult();
        await fixture.Service.WrapAsync(OriginalProviderStream(fixture), Context() with { IsOriginalDownload = true },
            "deezer", "track-1", false, () => Task.FromResult<Song?>(null), default);
        Assert.Equal(fixture.Bytes, await fixture.Response.Content.ReadAsByteArrayAsync());
        fixture.Response.Dispose();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(9));
        using (var replay = (await OpenOriginalAsync(fixture))!.Response)
            Assert.Equal(fixture.Bytes, await replay.Content.ReadAsByteArrayAsync());
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await OpenOriginalAsync(fixture));
        Assert.Null(fixture.PublishedPath);
    }

    private static ProtocolProviderStream OriginalProviderStream(ProgressiveFixture fixture)
    {
        var media = new ProviderMediaFormat("audio/flac", "flac", "flac");
        return new(fixture.Response, new("original", new Uri("https://media.example.test/original"),
            DateTimeOffset.UtcNow.AddMinutes(1), false, false, media, ProviderStreamRetryBehavior.DoNotRetry),
            "deezer", "track-1", fixture.Scope.ProviderAccountId, ServingAccountRevision: 4);
    }

    private static Task<ProtocolProviderStream?> OpenOriginalAsync(ProgressiveFixture fixture) => fixture.Service.TryOpenAsync(
        fixture.Track, fixture.Scope, default, 4, originalDownload: true);
}
