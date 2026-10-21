using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("detail")]
    [InlineData("list")]
    [InlineData("playback")]
    public async Task OfflineDownload_FinampReadablePathsAreDistinctForRepeatedTitlesAndDiscs(string surface)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var tracks = new[] { DownloadTrack("42", 1), DownloadTrack("43", 2), DownloadTrack("44", 2) };
        foreach (var song in tracks)
            gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", song.ExternalId!))
                .ReturnsAsync(song);
        using var factory = DownloadFactory("Jellyfin", gateway.Object);
        using var client = factory.CreateClient();
        var paths = new List<string>();
        foreach (var song in tracks)
        {
            var path = surface switch
            {
                "list" => $"/Items?Ids={song.Id}&Fields=MediaSources,Path&api_key=fixture-key",
                "playback" => $"/Items/{song.Id}/PlaybackInfo?api_key=fixture-key",
                _ => $"/Users/user-1/Items/{song.Id}?api_key=fixture-key"
            };
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = surface == "list" ? body.RootElement.GetProperty("Items")[0] : body.RootElement;
            var sources = JsonSerializer.Deserialize<DownloadMediaSource[]>(item.GetProperty("MediaSources"))!;
            var source = Assert.Single(sources);
            Assert.Equal("Default", source.Type);
            Assert.Equal(song.Id, source.Id);
            Assert.False(string.IsNullOrWhiteSpace(source.Name));
            Assert.Null(source.Container);
            Assert.False(string.IsNullOrWhiteSpace(source.Path));
            Assert.DoesNotContain(".flac", source.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Same", source.Name);
            // Finamp chooses the default source name and appends only the first eight ID characters.
            var fileName = Regex.Replace(source.Name!, "[/\\\\?<>:*|.\"]", "_") + "_" + song.Id[..8];
            var extension = source.Container == null ? "" : "." + source.Container;
            if (surface != "playback")
            {
                var webPath = item.GetProperty("Path").GetString()!;
                Assert.Equal(source.Name + extension, webPath.Replace('\\', '/').Split('/').Last());
            }
            paths.Add($"Finamp/Artist/Album/{fileName}{extension}");
        }
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, path => Assert.True(System.Text.Encoding.UTF8.GetByteCount(path.Split('/').Last()) <= 255));
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/cache/track.FLAC", "flac")]
    [InlineData("/cache/track.mp3", "mp3")]
    [InlineData("/cache/track.m4a", "m4a")]
    [InlineData("/cache/track.opus", "opus")]
    [InlineData("/cache/track.ogg", "ogg")]
    [InlineData("/cache/track.part", null)]
    [InlineData(null, null)]
    public void OfflineDownload_KnownOriginalContainerIsSharedAcrossItemAndPlayback(string? localPath, string? container)
    {
        var song = DownloadTrack("42", 1);
        song.LocalPath = localPath;
        var builder = new JellyfinResponseBuilder();
        var item = builder.ConvertSongToJellyfinItem(song, null, null, includeAllFields: true);
        var listing = JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(item))!;
        var playback = JsonSerializer.Deserialize<DownloadMediaSource[]>(JsonSerializer.Serialize(builder.CreatePlaybackMediaSources(song)))!;
        var source = Assert.Single(listing.MediaSources!);
        Assert.Equal(container, listing.Container);
        Assert.Equal(container, source.Container);
        Assert.Equal(container, Assert.Single(playback).Container);
        Assert.Equal(source.Name, playback[0].Name);
        Assert.Equal(source.Path, listing.Path);
        if (container != null) Assert.EndsWith("." + container, source.Path);
        else Assert.DoesNotContain(".flac", source.Path ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OfflineDownload_SourceNamesAreStableSafeAndBounded()
    {
        var song = DownloadTrack("42", 1);
        song.Title = "../CON:<>|*?\"\\" + string.Concat(Enumerable.Repeat("🎵", 200));
        var builder = new JellyfinResponseBuilder();
        DownloadMediaSource Source() => Assert.Single(JsonSerializer.Deserialize<DownloadMediaSource[]>(
            JsonSerializer.Serialize(builder.CreatePlaybackMediaSources(song)))!);
        var source = Source();
        Assert.Equal(source.Name, Source().Name);
        Assert.DoesNotContain(source.Name!, character => char.IsControl(character) || "/\\?<>:*|.\"".Contains(character));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(source.Name! + "_ext-deez.flac") <= 255);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task OfflineDownload_WebUnknownContainerStillHasAPlayableDirectUrl(string method)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([73, 68, 51, 4]) };
        upstream.Content.Headers.ContentType = new("audio/mpeg");
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42",
                allstarr.Core.Capabilities.ProviderAudioQuality.Any, null, method == "HEAD"))
            .ReturnsAsync(new ProtocolProviderStream(upstream, new allstarr.Core.Capabilities.ProviderStreamLease(
                "web", new Uri("https://media.example.test/original"), DateTimeOffset.UtcNow.AddMinutes(1),
                true, true, new("audio/mpeg", "mp3", "mp3"), allstarr.Core.Capabilities.ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42"));
        using var factory = DownloadFactory("Jellyfin", gateway.Object);
        using var client = factory.CreateClient();
        // Web appends a dot even when the nullable source container is unknown.
        using var request = new HttpRequestMessage(new HttpMethod(method),
            "/Audio/ext-deezer-song-42/stream.?Static=true&MediaSourceId=ext-deezer-song-42&ApiKey=fixture-key");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(method == "HEAD" ? [] : new byte[] { 73, 68, 51, 4 }, await response.Content.ReadAsByteArrayAsync());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    private static Song DownloadTrack(string id, int disc) => new()
    {
        Id = "ext-deezer-song-" + id,
        ExternalId = id,
        ExternalProvider = "deezer",
        Title = "Same title",
        Album = "Album",
        AlbumId = "ext-deezer-album-7",
        Artist = "Artist",
        AlbumArtist = "Artist",
        Track = 1,
        DiscNumber = disc
    };

    private sealed record DownloadItem(string? Id, string? Container, string? Path, DownloadMediaSource[]? MediaSources);
    private sealed record DownloadMediaSource(string? Id, string? Name, string? Container, string? Path, string? Type);
}
