using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class CommunityWordLyricsTests
{
    private const string Ttml = "<tt xmlns='http://www.w3.org/ns/ttml'><body dur='3:18.461'><div><p begin='5.376' end='7.018'><span begin='5.376' end='5.900'>Synthetic </span><span begin='5.900' end='7.018'>words</span></p></div></body></tt>";

    [Fact]
    public async Task AmllUsesLatestExactIsrcAndReusesIndexAndLyrics()
    {
        using var handler = new Handler((uri, _) => uri.AbsolutePath.EndsWith(".jsonl", StringComparison.Ordinal)
            ? Index("old.ttml", "Fixture", "USXXX0000001") + "\n" + Index("latest.ttml", "Fixture", "USXXX0000001") : Ttml);
        var adapter = new AmllLyricsCapabilityAdapter(new HttpClient(handler), new TestMemoryApplicationCache());
        var first = (await adapter.FetchLyricsAsync(Context("amll"), Request("amll"))).RequireValue();
        var second = (await adapter.FetchLyricsAsync(Context("amll"), Request("amll"))).RequireValue();
        Assert.Equal(2, handler.Calls);
        Assert.EndsWith("/raw-lyrics/latest.ttml", handler.Uris[1].AbsolutePath);
        Assert.Equal(2, Assert.Single(first.TimedLines!).Segments!.Count);
        Assert.Equal(first.Revision, second.Revision);
    }

    [Theory]
    [InlineData("Other song", "USXXX0000002", false)]
    [InlineData("Fixture", "USXXX0000002", false)]
    [InlineData("Fixture", "", true)]
    public async Task AmllMetadataFallbackRequiresIdentityAndDuration(string title, string isrc, bool expected)
    {
        using var handler = new Handler((uri, _) => uri.AbsolutePath.EndsWith(".jsonl", StringComparison.Ordinal)
            ? Index("fixture.ttml", title, isrc) : Ttml);
        var value = (await new AmllLyricsCapabilityAdapter(new HttpClient(handler), null)
            .FetchLyricsAsync(Context("amll"), Request("amll"))).RequireValue();
        Assert.Equal(expected ? ProviderLyricsAvailabilityState.Available : ProviderLyricsAvailabilityState.Unavailable, value.Availability);
    }

    [Theory]
    [InlineData("apple", "appleMusicId", "123")]
    [InlineData("spotify", "spotifyId", "spotify-song")]
    [InlineData("netease", "ncmMusicId", "456")]
    public async Task AmllFindsExactPlatformIdentifiers(string prefix, string key, string id)
    {
        var index = JsonSerializer.Serialize(new { metadata = new object[] { new object[] { key, new[] { id } } }, rawLyricFile = "platform.ttml" });
        using var handler = new Handler((uri, _) => uri.AbsolutePath.EndsWith(".jsonl", StringComparison.Ordinal) ? index : Ttml);
        var request = new ProviderLyricsRequest(Guid.CreateVersion7(), new("amll", ProviderResourceKind.Track, prefix + ":" + id));
        var result = (await new AmllLyricsCapabilityAdapter(new HttpClient(handler), null)
            .FetchLyricsAsync(Context("amll"), request)).RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.Availability);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AmllCacheSeparatesDifferentPlatformSongsWithoutMetadata()
    {
        var index = JsonSerializer.Serialize(new { metadata = new object[] { new object[] { "appleMusicId", new[] { "123" } } }, rawLyricFile = "first.ttml" }) + "\n" +
            JsonSerializer.Serialize(new { metadata = new object[] { new object[] { "appleMusicId", new[] { "456" } } }, rawLyricFile = "second.ttml" });
        using var handler = new Handler((uri, _) => uri.AbsolutePath.EndsWith(".jsonl", StringComparison.Ordinal) ? index :
            Ttml.Replace("Synthetic", uri.AbsolutePath.Contains("first", StringComparison.Ordinal) ? "First" : "Second"));
        var adapter = new AmllLyricsCapabilityAdapter(new HttpClient(handler), new TestMemoryApplicationCache());
        var first = (await adapter.FetchLyricsAsync(Context("amll"), new(Guid.CreateVersion7(), new("amll", ProviderResourceKind.Track, "apple:123")))).RequireValue();
        var second = (await adapter.FetchLyricsAsync(Context("amll"), new(Guid.CreateVersion7(), new("amll", ProviderResourceKind.Track, "apple:456")))).RequireValue();
        Assert.Contains("First", first.Content);
        Assert.Contains("Second", second.Content);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("amll")]
    [InlineData("lrcmux")]
    public async Task MetadataWithoutDurationOrIsrcCannotSelectAnAmbiguousRecording(string provider)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException());
        IProviderLyricsCapability adapter = provider == "amll" ? new AmllLyricsCapabilityAdapter(new HttpClient(handler), null) :
            new LrcMuxLyricsCapabilityAdapter(new HttpClient(handler), null);
        var request = new ProviderLyricsRequest(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"),
            trackTitle: "Fixture", artistNames: ["Artist"]);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, (await adapter.FetchLyricsAsync(Context(provider), request)).RequireValue().Availability);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AmllRejectsIndexPathsOutsideTheRawLyricsDirectory()
    {
        using var handler = new Handler((_, _) => Index("../outside.ttml", "Fixture", "USXXX0000001"));
        var value = (await new AmllLyricsCapabilityAdapter(new HttpClient(handler), null)
            .FetchLyricsAsync(Context("amll"), Request("amll"))).RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, value.Availability);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("USXXX0000001", "Fixture", 198, true)]
    [InlineData("USXXX0000002", "Fixture", 198, false)]
    [InlineData("USXXX0000001", "Other song", 198, false)]
    [InlineData("USXXX0000001", "Fixture", 230, false)]
    public async Task LrcmuxKeepsWordTimesOnlyForTheRequestedRecording(string isrc, string title, int duration, bool expected)
    {
        var json = JsonSerializer.Serialize(new
        {
            track = new { isrc, title, artist = "Artist", duration }, meta = new { level = "word" },
            lines = new[] { new { text = "Synthetic words", start = 5623, end = 6711,
                words = new[] { new { text = "Synthetic ", start = 5623, end = 6000 }, new { text = "words", start = 6000, end = 6711 } } } }
        });
        using var handler = new Handler((_, _) => json);
        var value = (await new LrcMuxLyricsCapabilityAdapter(new HttpClient(handler), null)
            .FetchLyricsAsync(Context("lrcmux"), Request("lrcmux"))).RequireValue();
        Assert.Equal(expected ? ProviderLyricsAvailabilityState.Available : ProviderLyricsAvailabilityState.Unavailable, value.Availability);
        if (expected) Assert.Equal(TimeSpan.FromMilliseconds(5623), Assert.Single(value.TimedLines!).Segments![0].Start);
    }

    private static string Index(string file, string title, string isrc) => JsonSerializer.Serialize(new
    { metadata = new object[] { new object[] { "musicName", new[] { title } }, new object[] { "artists", new[] { "Artist" } }, new object[] { "isrc", new[] { isrc } } }, rawLyricFile = file });
    private static ProviderLyricsRequest Request(string provider) => new(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"),
        trackTitle: "Fixture", artistNames: ["Artist"], durationSeconds: 198, isrc: "USXXX0000001");
    private static ProviderExecutionContext Context(string provider) => new(new(ProviderActorKind.PublicRead, null), provider, null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, false, [provider]), "lyrics-test", "lyrics-test", DateTimeOffset.UtcNow.AddMinutes(1), default);
    private sealed class Handler(Func<Uri, int, string> response) : HttpMessageHandler
    {
        public int Calls;
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri!, ++Calls)) });
        }
    }
}
