using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers;
using allstarr.Core.Providers.Qobuz;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class QobuzProviderPagingTests
{
    [Theory]
    [InlineData(503, 503)]
    [InlineData(1, 503)]
    [InlineData(900, 500)]
    public async Task Playlist_PagesBeyondFirstLimitAndIgnoresInconsistentTotals(int total, int available)
    {
        var offsets = new List<int>();
        var provider = Provider(request =>
        {
            var offset = Offset(request);
            offsets.Add(offset);
            var count = Math.Max(0, Math.Min(500, available - offset));
            return Page(offset, count, total, 500);
        });
        var tracks = await provider.GetPlaylistTracksAsync("qobuz", "playlist");
        Assert.Equal(available, tracks.Count);
        Assert.Equal([0, 500], offsets);
        Assert.Equal(Enumerable.Range(1, available), tracks.Select(track => track.Track!.Value));
        Assert.All(tracks, track => Assert.Null(track.DiscNumber));
    }

    [Fact]
    public async Task TypedPlaylist_PreservesDuplicateEntriesAndGlobalPositionsAcrossPages()
    {
        var provider = Provider(request => Page(Offset(request), Offset(request) < 4 ? 2 : 1, 5, 2));
        var playlists = new CatalogPlaylistCapability("qobuz", provider);
        var result = await playlists.GetPlaylistTracksAsync(Context(withAccount: true), new(
            new("qobuz", ProviderResourceKind.Playlist, "playlist"), new(3, "2")));
        var page = result.RequireValue().Tracks;
        Assert.Equal([2, 3, 4], page.Items.Select(item => item.Position));
        Assert.All(page.Items, item => Assert.Equal("7", item.TrackId.Value));
        Assert.Equal([3, 4, 5], page.Items.Select(item => item.Metadata!.TrackNumber));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Playlist_StopsWhenProviderReturnsAnEmptyFirstPage()
    {
        var calls = 0;
        var provider = Provider(_ => { calls++; return Page(0, 0, 400, 500); });
        Assert.Empty(await provider.GetPlaylistTracksAsync("qobuz", "playlist"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Playlist_RejectsRepeatedOffsetWithoutPublishingPartialSuccess()
    {
        var provider = Provider(_ => Page(0, 2, 4, 2));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GetPlaylistTracksAsync("qobuz", "playlist"));
        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playlist_DoesNotReturnPartialResultsAfterALaterPageFails(bool missingPage)
    {
        var provider = Provider(request => Offset(request) == 0 ? Page(0, 2, 4, 2) :
            missingPage ? new(HttpStatusCode.NotFound) : Json(new { error = "unavailable" }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetPlaylistTracksAsync("qobuz", "playlist"));
    }

    [Fact]
    public async Task Playlist_BoundsAnEndlessRemotePageEvenWhenOffsetsAreOmitted()
    {
        var calls = 0;
        var provider = Provider(_ =>
        {
            calls++;
            return Json(new { id = "playlist", name = "Playlist", tracks = new { limit = 1, items = new[] { Track() } } });
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetPlaylistTracksAsync("qobuz", "playlist"));
        Assert.Equal(200, calls);
    }

    [Fact]
    public async Task Playlist_CancelsBetweenPages()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var provider = Provider(_ =>
        {
            calls++;
            cancellation.Cancel();
            return Page(0, 2, 4, 2);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetPlaylistTracksAsync("qobuz", "playlist", cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AlbumDetail_FetchesRemainingTracksAndKeepsAlbumOrdering()
    {
        var offsets = new List<int>();
        var provider = Provider(request =>
        {
            var offset = Offset(request); offsets.Add(offset);
            return Json(new
            {
                id = "album",
                title = "Album",
                tracks_count = 3,
                artist = new { id = 1, name = "Artist" },
                tracks = new
                {
                    offset,
                    limit = 2,
                    total = 3,
                    items = Enumerable.Range(offset, offset == 0 ? 2 : 1)
                    .Select(index => new { id = index + 1, title = "Track", track_number = index + 1, media_number = 1 })
                }
            });
        });
        var album = await provider.GetAlbumAsync("qobuz", "album");
        Assert.Equal([0, 2], offsets);
        Assert.Equal([1, 2, 3], album!.Songs.Select(track => track.Track));
        Assert.All(album.Songs, track => Assert.Equal("Album", track.Album));
    }

    [Fact]
    public async Task SelectedAccountsStayIsolatedWhilePublicCatalogSendsNoToken()
    {
        var observed = new System.Collections.Concurrent.ConcurrentBag<string?>();
        var provider = Provider(request =>
        {
            observed.Add(request.Headers.TryGetValues("X-User-Auth-Token", out var values) ? values.Single() : null);
            return Json(new { tracks = new { items = new[] { Track() } } });
        });
        var request = new ProviderMetadataSearchRequest("fixture", new(1));
        var first = Context(true);
        var second = Context(true);
        var outcomes = await Task.WhenAll(
            provider.SearchTracksAsync(first, request), provider.SearchTracksAsync(second, request),
            provider.SearchTracksAsync(Context(false), request));
        Assert.All(outcomes, result => Assert.True(result.IsSuccess));
        Assert.Contains(null, observed);
        Assert.Contains(first.Account!.AccountId.ToString(), observed);
        Assert.Contains(second.Account!.AccountId.ToString(), observed);
        Assert.Equal(3, observed.Distinct().Count());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ProviderErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorKind.TransientFailure)]
    public async Task Catalog_ClassifiesProviderFailure(HttpStatusCode status, ProviderErrorKind expected)
    {
        var calls = 0;
        var provider = Provider(_ => { calls++; return new(status); });
        var outcome = await provider.SearchTracksAsync(Context(true), new("fixture", new(1)));
        Assert.Equal(expected, outcome.Error!.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RevokedCredentialLeaseDoesNotSendAnyProviderRequest()
    {
        var provider = Provider(_ => throw new InvalidOperationException("HTTP must not run"), new DeniedSecrets());
        var result = await provider.SearchTracksAsync(Context(true), new("fixture", new(1)));
        Assert.Equal(ProviderErrorKind.Forbidden, result.Error!.Kind);
    }

    private sealed class DeniedSecrets : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken) => throw new UnauthorizedAccessException();
    }

    private static QobuzProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> respond, IProviderAccountSecretAccessor? secrets = null)
    {
        var http = new HttpClient(new Handler(respond));
        var factory = Mock.Of<IHttpClientFactory>(item => item.CreateClient(It.IsAny<string>()) == http);
        var bundle = new Mock<QobuzBundleService>(factory, NullLogger<QobuzBundleService>.Instance);
        bundle.Setup(item => item.GetAppIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync("fixture-app");
        return new(http, bundle.Object, secrets ?? new Secrets(), NullLogger<QobuzProvider>.Instance);
    }

    private static ProviderExecutionContext Context(bool withAccount)
    {
        var user = Guid.CreateVersion7();
        return new(new(ProviderActorKind.User, user, new("jellyfin", "primary", "fixture")), "qobuz",
            withAccount ? new ProviderAccountContext(Guid.CreateVersion7(), "qobuz", ProviderAccountScope.Personal,
                3, ownerUserId: user, secretReferenceId: Guid.CreateVersion7()) : null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false), ProviderExplicitContentPolicy.Allow,
                false, false, false, ["qobuz"]), "catalog", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
    }

    private static int Offset(HttpRequestMessage request) =>
        QueryHelpers.ParseQuery(request.RequestUri!.Query).TryGetValue("offset", out var value) &&
        int.TryParse(value, out var offset) ? offset : 0;
    private static object Track() => new { id = 7, title = "Repeated", duration = 100, performer = new { id = 1, name = "Artist" } };
    private static HttpResponseMessage Page(int offset, int count, int total, int limit) => Json(new
    {
        id = "playlist",
        name = "Playlist",
        tracks_count = total,
        tracks = new { offset, total, limit, items = Enumerable.Repeat(Track(), count) }
    });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request);
        }
    }
    private sealed class Secrets : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(3, account.Revision);
            return operation(JsonSerializer.SerializeToUtf8Bytes(new { userAuthToken = account.AccountId.ToString(), userId = "fixture-user" }));
        }
    }
}
