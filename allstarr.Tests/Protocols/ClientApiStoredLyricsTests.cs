using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed class ClientApiStoredLyricsTests
{
    [Theory]
    [InlineData("lrcred")]
    [InlineData("lrclib")]
    public async Task StoredSearchUsesTheLyricsRoutesSongDurationForLastLineTiming(string provider)
    {
        var song = new Song { Title = "Fixture", Artist = "Artist", Duration = 10 };
        var cache = new Mock<IApplicationCache>();
        cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.IsAny<string>())).ReturnsAsync(new ProviderLyricsResult(
            ProviderLyricsAvailabilityState.Available, provider, ProviderLyricsFormat.LineTimed, "[00:01.00]First\n[00:02.00]Last"));
        cache.Setup(item => item.GetStringAsync(It.IsAny<string>())).ReturnsAsync(JsonSerializer.Serialize(new LyricsInfo
        { Source = provider, Duration = 999, SyncedLyrics = "[00:01.00]First\n[00:02.00]Last" }));
        var policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty
            .Add(ProviderCapabilityKind.Lyrics, [provider]), ImmutableHashSet<string>.Empty, "lossless", .07);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener", null,
            "stored-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var observed = new List<RichLyrics>();
        await StoredItemLyrics.ReadAsync(cache.Object, song, null, null, null, policy, "http://unused.invalid", context, [], [], default, observed.Add);
        Assert.Equal(10000, Assert.Single(observed).Lines[^1].EndMs);
    }

    [Theory]
    [InlineData("lrclib:102", "Chosen verse", "lrclib:102")]
    [InlineData("lrclib:missing", "Best verse", "lrclib")]
    [InlineData("lrclib", "Best verse", "lrclib")]
    public async Task StoredLyricsVersionPinUsesCachedVersionOrProviderBest(string pin, string expectedText, string expectedSource)
    {
        var song = new Song { Title = "Fixture", Artist = "Artist", Duration = 240 };
        var best = new LyricsInfo { Source = "lrclib", PlainLyrics = "Best verse", Metadata = new("101", "Fixture", "Artist", null, 240000) };
        var chosen = new LyricsInfo { Source = "lrclib", PlainLyrics = "Chosen verse", Metadata = new("102", "Fixture (Live)", "Artist", null, 271000) };
        best.Versions = [new() { Source = "lrclib", PlainLyrics = best.PlainLyrics, Metadata = best.Metadata }, chosen];
        var cache = new Mock<IApplicationCache>();
        cache.Setup(item => item.GetStringAsync(It.IsAny<string>())).ReturnsAsync(JsonSerializer.Serialize(best));
        var policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty
            .Add(ProviderCapabilityKind.Lyrics, ["lrclib"]), ImmutableHashSet<string>.Empty, "lossless", .07);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener", null,
            "stored-version", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var observed = new List<RichLyrics>();
        var result = await StoredItemLyrics.ReadAsync(cache.Object, song, null, pin, null, policy, "http://unused.invalid", context, [], [], default, observed.Add);
        Assert.True(result.Available);
        Assert.Equal(expectedSource, result.Source);
        Assert.Contains(observed, rich => rich.Source.Id == expectedSource && rich.Lines[0].Text == expectedText);
    }

    [Fact]
    public async Task StoredGeniusLyricsDecodeExistingCacheWithoutFetching()
    {
        var song = new Song { Title = "Fixture", Artist = "Artist" };
        var cache = new TestMemoryApplicationCache();
        var suffix = CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
        {
            song.Isrc,
            TrackTitle = "Fixture",
            ArtistNames = new[] { "Artist" },
            AlbumTitle = (string?)null,
            DurationSeconds = (int?)null
        }));
        await cache.SetAsync("lyrics:v3:genius:" + suffix, new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
            "genius", ProviderLyricsFormat.PlainText, "I&#x27;ve heard there&#x27;s more"));
        var policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty
            .Add(ProviderCapabilityKind.Lyrics, ["genius"]), ImmutableHashSet<string>.Empty, "lossless", .07);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener", null,
            "stored-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var observed = new List<RichLyrics>();
        var result = await StoredItemLyrics.ReadAsync(cache, song, null, null, null, policy, "http://unused.invalid", context, [], [], default, observed.Add);
        Assert.True(result.Available);
        Assert.Equal("I've heard there's more", Assert.Single(observed).Lines[0].Text);
        var stored = await cache.GetAsync<ProviderLyricsResult>("lyrics:v3:genius:html-decoded:v1:" + suffix);
        Assert.Equal("I've heard there's more", stored!.Content);
    }

    [Theory]
    [InlineData(null, "word", "lrcred")]
    [InlineData(null, "line", "kept")]
    [InlineData("kept", "word", "kept")]
    [InlineData("lrcred", "word", "lrcred")]
    public async Task StoredAvailabilityUsesQualityAndPreservesChosenSources(string? pin, string serviceFormat, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "stored-lyrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "track.flac");
            await File.WriteAllTextAsync(audio, "fixture");
            await File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"), "[00:01.000]Kept words");
            var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
            cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.Is<string>(key => key.StartsWith("lyrics:v3:lrcred:"))))
                .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrcred", ProviderLyricsFormat.LineTimed,
                    serviceFormat == "word" ? "[00:01.000]<00:01.000>Service <00:01.500>words" : "[00:01.000]Service words"));
            var policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty
                .Add(ProviderCapabilityKind.Lyrics, ["kept", "lrcred"]), ImmutableHashSet<string>.Empty, "lossless", .07);
            var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener", null,
                "stored-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var result = await StoredItemLyrics.ReadAsync(cache.Object, new Song { Title = "Fixture", Artist = "Artist" }, null,
                pin, audio, policy, "http://unused.invalid", context, [], [], default);
            Assert.True(result.Available);
            Assert.Equal(expected, result.Source);
            Assert.Equal(expected == "kept" ? "line" : serviceFormat, result.Format);
            if (pin == "kept") cache.VerifyNoOtherCalls();
            else cache.VerifyAll();
        }
        finally { Directory.Delete(root, true); }
    }
}
