using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Qobuz;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class QobuzProviderSearchTests
{
    [Theory]
    [InlineData("\"parental_warning\":true,\"upc\":\"0123456789012\",", true, "0123456789012")]
    [InlineData("\"parental_warning\":false,\"upc\":123456789012,", false, "123456789012")]
    [InlineData("\"parental_warning\":null,", null, null)]
    [InlineData("\"parental_warning\":\"false\",", null, null)]
    [InlineData("", null, null)]
    public async Task Album_search_and_lookup_preserve_nullable_source_evidence(
        string evidence, bool? expectedExplicit, string? expectedUpc)
    {
        var albumJson = $$$"""
            {"id":"album-1","title":"Album","version":"Deluxe",{{{evidence}}}
             "artist":{"id":1,"name":"Artist"},"tracks_count":0,"tracks":{"items":[]}}
            """;
        using var http = new HttpClient(new Handler(request => Json(
            request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? "{\"albums\":{\"items\":[" + albumJson + "]}}" : albumJson)));
        var factory = Mock.Of<IHttpClientFactory>(item => item.CreateClient(It.IsAny<string>()) == http);
        var bundles = new Mock<QobuzBundleService>(factory, NullLogger<QobuzBundleService>.Instance);
        bundles.Setup(item => item.GetAppIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync("fixture-app");
        var catalog = new QobuzProvider(http, bundles.Object, new Secrets(), NullLogger<QobuzProvider>.Instance);

        var searched = Assert.Single((await catalog.SearchAlbumsAsync(Context(true), new("album", new(1)))).RequireValue().Items);
        var full = (await catalog.GetAlbumAsync(Context(true), new(new("qobuz", ProviderResourceKind.Album, "album-1")))).RequireValue();

        foreach (var album in new[] { searched, full })
        {
            Assert.Equal(expectedExplicit, album.IsExplicit);
            Assert.Equal(expectedUpc, album.Upc);
            Assert.Equal("Deluxe", album.Edition);
            Assert.Null(album.ReleaseType);
            Assert.Null(album.ExclusionReason);
        }
    }

    [Fact]
    public async Task SearchTracks_UsesOnlyCatalogEndpointUntilPlaybackRequestsLease()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            Assert.Equal("fixture-token", request.Headers.GetValues("X-User-Auth-Token").Single());
            return request.RequestUri!.AbsolutePath switch
            {
                "/api.json/0.2/track/search" => Json("""
                    {"tracks":{"items":[
                      {"id":42,"title":"First","duration":201,"streamable":true,
                       "performer":{"id":1,"name":"Artist"}},
                      {"id":43,"title":"Second","duration":202,"streamable":false,
                       "performer":{"id":1,"name":"Artist"}}
                    ]}}
                    """),
                "/api.json/0.2/track/getFileUrl" => Json("""
                    {"url":"https://media.example.test/audio.flac","mime_type":"audio/flac",
                     "bit_depth":16,"sampling_rate":44.1}
                    """),
                _ => throw new InvalidOperationException("Unexpected Qobuz endpoint.")
            };
        }));
        var factory = Mock.Of<IHttpClientFactory>(item => item.CreateClient(It.IsAny<string>()) == http);
        var bundles = new Mock<QobuzBundleService>(factory, NullLogger<QobuzBundleService>.Instance);
        bundles.Setup(item => item.GetAppIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync("fixture-app");
        bundles.Setup(item => item.GetSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QobuzSigningCredentials("fixture-app", ["fixture-secret"]));
        var secrets = new Secrets();
        var catalog = new QobuzProvider(http, bundles.Object, secrets, NullLogger<QobuzProvider>.Instance);
        var streaming = new QobuzStreamingCapabilityAdapter(http, secrets,
            new QobuzMediaClient(http, bundles.Object, NullLogger<QobuzMediaClient>.Instance), "FLAC_16");

        var outcome = await catalog.SearchTracksAsync(Context(true), new("Artist & First", new(20)));

        Assert.True(outcome.IsSuccess);
        var tracks = outcome.RequireValue().Items;
        Assert.Equal(["42", "43"], tracks.Select(track => track.Id.Value));
        Assert.Equal(["First", "Second"], tracks.Select(track => track.Title));
        var search = Assert.Single(requests);
        Assert.Equal("/api.json/0.2/track/search", search.AbsolutePath);
        var searchQuery = QueryHelpers.ParseQuery(search.Query);
        Assert.Equal("Artist & First", searchQuery["query"].ToString());
        Assert.Equal("20", searchQuery["limit"].ToString());
        bundles.Verify(item => item.GetSigningCredentialsAsync(It.IsAny<CancellationToken>()), Times.Never);

        var playback = await streaming.GetStreamLeaseAsync(Context(true), new(tracks[0].Id));

        Assert.True(playback.IsSuccess);
        Assert.Equal(new Uri("https://media.example.test/audio.flac"), playback.RequireValue().ProtectedSourceUri);
        Assert.Equal(["/api.json/0.2/track/search", "/api.json/0.2/track/getFileUrl"],
            requests.Select(uri => uri.AbsolutePath));
        Assert.Equal("42", QueryHelpers.ParseQuery(requests[1].Query)["track_id"].ToString());
        bundles.Verify(item => item.GetSigningCredentialsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchTracks_RequiresAnAccountBeforeSendingCatalogRequests()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected request.")));
        var factory = Mock.Of<IHttpClientFactory>(item => item.CreateClient(It.IsAny<string>()) == http);
        var bundles = new Mock<QobuzBundleService>(factory, NullLogger<QobuzBundleService>.Instance);
        bundles.Setup(item => item.GetAppIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync("fixture-app");
        var catalog = new QobuzProvider(http, bundles.Object, new Secrets(), NullLogger<QobuzProvider>.Instance);
        var registration = QobuzDownloadCapabilityAdapter.CreateRegistration(
            Mock.Of<IProviderDownloadCapability>(), Mock.Of<IProviderStreamingCapability>(), catalog,
            Mock.Of<IProviderPlaylistCapability>());

        var outcome = await catalog.SearchTracksAsync(Context(false), new("Artist", new(20)));

        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, outcome.Error?.Kind);
        Assert.Equal(ProviderAccountRequirement.Required,
            registration.Descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Metadata).AccountRequirement);
        bundles.Verify(item => item.GetAppIdAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ProviderExecutionContext Context(bool withAccount)
    {
        var user = Guid.CreateVersion7();
        return new(new(ProviderActorKind.User, user, new("jellyfin", "primary", "fixture")), "qobuz",
            withAccount ? new ProviderAccountContext(Guid.CreateVersion7(), "qobuz", ProviderAccountScope.Personal,
                1, ownerUserId: user, secretReferenceId: Guid.CreateVersion7()) : null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false), ProviderExplicitContentPolicy.Allow,
                false, false, false, ["qobuz"]), "search", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Secrets : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation(JsonSerializer.SerializeToUtf8Bytes(new { userAuthToken = "fixture-token", userId = "fixture-user" }));
        }
    }
}
