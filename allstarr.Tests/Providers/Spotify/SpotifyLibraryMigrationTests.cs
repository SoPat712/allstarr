using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Tests;

public sealed class SpotifyLibraryMigrationTests
{
    [Theory]
    [InlineData("saved-albums", "album", "Albums")]
    [InlineData("followed-artists", "artist", "Artists")]
    public async Task LibraryReadsEveryPageWithAccountTokenAndPreservesMetadata(string kind, string type, string filter)
    {
        var handler = new Fixture(type);
        var reader = new SpotifyPathfinderPlaylistClient(new HttpClient(handler));
        var first = (await reader.GetLibraryResourcesAsync("fixture-token", kind, new(1), default)).RequireValue();
        Assert.Equal(2, first.Total);
        Assert.Equal("1", first.NextCursor);
        var item = Assert.Single(first.Items);
        Assert.Equal("fixture0", item.SourceId);
        Assert.Equal("Fixture release", item.Title);
        Assert.Equal("https://i.scdn.co/image/fixture", item.ArtworkUrl);
        if (type == "album")
        {
            Assert.Equal("Fixture artist", Assert.Single(item.Artists));
            Assert.Equal("artist0", Assert.Single(item.ArtistIds));
            Assert.Equal("2024-03-01", item.ReleaseDate);
            Assert.Equal("012345678901", item.Upc);
            Assert.Equal(12, item.TrackCount);
        }
        var last = (await reader.GetLibraryResourcesAsync("fixture-token", kind, new(1, first.NextCursor), default)).RequireValue();
        Assert.Null(last.NextCursor);
        Assert.Equal("fixture1", Assert.Single(last.Items).SourceId);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer fixture-token", request.Token);
            Assert.Contains($"\"filters\":[\"{filter}\"]", request.Url);
            Assert.Contains("operationName=libraryV3", request.Url);
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("long-name")]
    [InlineData("large-page")]
    [InlineData("long-edition")]
    public async Task IncompatiblePagesFailInsteadOfImportingAnEmptyOrPartialLibrary(string failure)
    {
        var reader = new SpotifyPathfinderPlaylistClient(new HttpClient(new Fixture("album", failure)));
        Assert.False((await reader.GetLibraryResourcesAsync("fixture-token", "saved-albums", new(1), default)).IsSuccess);
    }

    private sealed class Fixture(string type, string? failure = null) : HttpMessageHandler
    {
        public List<(string Url, string? Token)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add((url, request.Headers.Authorization?.ToString()));
            var offset = url.Contains("\"offset\":1", StringComparison.Ordinal) ? 1 : 0;
            var item = new
            {
                item = new
                {
                    data = new
                    {
                        uri = $"spotify:{type}:fixture{offset}",
                        name = failure == "long-name" ? new string('x', 501) : "Fixture release",
                        profile = new { name = "Fixture release" },
                        artists = new { items = new[] { new { uri = "spotify:artist:artist0", profile = new { name = "Fixture artist" } } } },
                        edition = failure == "long-edition" ? new string('x', 501) : null,
                        date = new { isoString = "2024-03-01" },
                        tracks = new { totalCount = 12 },
                        upc = "012345678901",
                        coverArt = new { sources = new[] { new { url = "https://i.scdn.co/image/fixture", width = 640 } } }
                    }
                }
            };
            object payload = failure == "missing" ? new { data = new { } } :
                new
                {
                    data = new
                    {
                        me = new
                        {
                            libraryV3 = new
                            {
                                totalCount = 2,
                                items = failure == "truncated" ? [] : Enumerable.Repeat(item, failure == "large-page" ? 2 : 1).ToArray()
                            }
                        }
                    }
                };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) });
        }
    }
}
