using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class GeniusCapabilityAdapterTests
{
    [Theory]
    [InlineData("I&#x27;ve heard there&#x27;s a song", "I've heard there's a song")]
    [InlineData("&quot;You &amp; me&quot; &lt;again&gt;", "\"You & me\" <again>")]
    [InlineData("We&#39;ll sing &#8212; &#x266B;", "We'll sing — ♫")]
    public async Task DecodesHtmlEntitiesBeforeReturningLyrics(string encoded, string expected)
    {
        using var handler = new Handler(encoded);
        using var http = new HttpClient(handler);
        var adapter = new GeniusCapabilityAdapter(http, null, TimeProvider.System, new(TimeProvider.System));

        var value = (await adapter.FetchLyricsAsync(Context(), Request())).RequireValue();

        Assert.Equal(ProviderLyricsAvailabilityState.Available, value.Availability);
        Assert.Equal(ProviderLyricsFormat.PlainText, value.Format);
        Assert.Equal(expected, value.Content);
        Assert.Equal(Revision(expected), value.Revision);
    }

    [Fact]
    public async Task CachesDecodedLyricsWithSectionsAndLineBreaksPreserved()
    {
        const string encoded = "  [Verse]\r\nI&#x27;ve heard\r\n\r\nThere&#x27;s more &amp; more\r\nLast &#x266B;12Embed\r\n3 Contributors";
        const string expected = "[Verse]\nI've heard\n\nThere's more & more\nLast ♫";
        using var handler = new Handler(encoded);
        using var http = new HttpClient(handler);
        var cache = new TestMemoryApplicationCache();
        var request = Request();
        var legacyKey = "lyrics:v3:genius:" + Hash(JsonSerializer.Serialize(new
        {
            request.Isrc,
            request.TrackTitle,
            request.ArtistNames,
            request.AlbumTitle,
            request.DurationSeconds
        }));
        await cache.SetAsync(legacyKey, new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
            "genius", ProviderLyricsFormat.PlainText, encoded, Revision(encoded)));
        var adapter = new GeniusCapabilityAdapter(http, cache, TimeProvider.System, new(TimeProvider.System));

        var first = (await adapter.FetchLyricsAsync(Context(), request)).RequireValue();
        var stored = await cache.GetAsync<ProviderLyricsResult>(Assert.Single(cache.GetKeysByPattern("lyrics:*"), key => key != legacyKey));
        var cached = (await adapter.FetchLyricsAsync(Context(), request)).RequireValue();

        Assert.Equal(expected, first.Content);
        Assert.NotNull(stored);
        Assert.Equal(expected, stored.Content);
        Assert.Equal(expected, cached.Content);
        Assert.Equal(Revision(expected), first.Revision);
        Assert.Equal(first.Revision, stored.Revision);
        Assert.Equal(first.Revision, cached.Revision);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PreservesProviderTitleAndArtistWithoutInventingAlbumOrDuration()
    {
        using var handler = new Handler("Words", includeMetadata: true);
        using var http = new HttpClient(handler);
        var result = (await new GeniusCapabilityAdapter(http, null, TimeProvider.System, new(TimeProvider.System))
            .FetchLyricsAsync(Context(), Request())).RequireValue();
        Assert.Equal("Fixture", result.Metadata?.Title);
        Assert.Equal("Artist", result.Metadata?.Artist);
        Assert.Null(result.Metadata?.Album);
        Assert.Null(result.Metadata?.DurationMs);
    }

    private static string Revision(string content) => "sha256:" + Hash(content);

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static ProviderLyricsRequest Request() =>
        new(Guid.CreateVersion7(), new("genius", ProviderResourceKind.Track, "lookup"),
            trackTitle: "Fixture", artistNames: ["Artist"]);

    private static ProviderExecutionContext Context() => new(new(ProviderActorKind.PublicRead, null), "genius", null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, false, ["genius"]), "lyrics-test", "lyrics-test", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private sealed class Handler(string lyrics, bool includeMetadata = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(includeMetadata ? (object)new { lyrics, title = "Fixture", artist = "Artist" } : new { lyrics })) });
        }
    }
}
