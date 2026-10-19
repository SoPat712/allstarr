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
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchTracks_UsesOnlyCatalogEndpointUntilPlaybackRequestsLease(bool authenticatedSearch)
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
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

        var outcome = await catalog.SearchTracksAsync(Context(authenticatedSearch), new("Artist & First", new(20)));

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
