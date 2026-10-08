using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class JellyfinPlaybackMetadataResolverTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();

    [Fact]
    public async Task ResolveAsyncUsesExactViewerAuthenticationAndFreshUserFilteredItemQueries()
    {
        var viewers = new List<string>();
        var resolver = CreateResolver(request =>
        {
            Assert.Equal("/Items", request.RequestUri!.AbsolutePath);
            var query = ParseQuery(request.RequestUri);
            var viewer = query["UserId"];
            viewers.Add(viewer);
            Assert.Equal($"viewer-token-{viewer}", request.Headers.GetValues("X-Emby-Token").Single());
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.Equal("item-1", query["Ids"]);
            Assert.Equal("true", query["Recursive"]);
            Assert.Equal("Audio", query["IncludeItemTypes"]);
            Assert.Contains("ProviderIds", query["Fields"]);
            return Item("item-1", $"Title for {viewer}");
        });

        var first = await resolver.ResolveAsync("item-1", Context("viewer-a"), default);
        var second = await resolver.ResolveAsync("item-1", Context("viewer-b"), default);
        var repeated = await resolver.ResolveAsync("item-1", Context("viewer-a"), default);

        Assert.Equal("Title for viewer-a", first!.Title);
        Assert.Equal("Title for viewer-b", second!.Title);
        Assert.Equal("Title for viewer-a", repeated!.Title);
        Assert.Equal(["viewer-a", "viewer-b", "viewer-a"], viewers);
        Assert.Equal("Track artist", first.Artist);
        Assert.Equal("Fixture album", first.Album);
        Assert.Equal("Fixture artist", first.AlbumArtist);
        Assert.Equal("11111111-1111-1111-1111-111111111111", first.RecordingMusicBrainzId);
        Assert.Equal(7, first.TrackNumber);
        Assert.Equal("/api/admin/downloads/artwork/item-1", first.CoverArtUrl);
    }

    [Fact]
    public async Task ResolveAsyncUsesAlbumArtworkWhenAudioItemHasNoPrimaryImage()
    {
        var resolver = CreateResolver(_ => Json("""
            { "Items": [{ "Id": "track-1", "Name": "Album track", "AlbumArtist": "Artist",
                          "AlbumId": "album-42", "AlbumPrimaryImageTag": "album-etag" }] }
            """));

        var metadata = await resolver.ResolveAsync("track-1", Context(), default);

        Assert.NotNull(metadata);
        Assert.Equal("/api/admin/downloads/artwork/album-42", metadata.CoverArtUrl);
    }

    [Fact]
    public async Task ContextlessMetadataAndArtworkNeverSelectConfiguredAdministrator()
    {
        var source = new ViewerPermissionSource();
        var requests = 0;
        var resolver = CreateResolver(_ => { requests++; return Item("item-1"); }, source: source);

        Assert.Null(await resolver.ResolveAsync("item-1", default));
        Assert.Null(await resolver.ResolveArtworkAsync("item-1", default));
        Assert.Equal(0, source.RequestCount);
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingViewerOrLibraryPermissionNeverFallsBackToConfiguredAdministrator(bool accessSucceeded, bool hasLibrary)
    {
        var source = new ViewerPermissionSource();
        var access = new ViewerLibraryAccess { Access = new(accessSucceeded, hasLibrary ? ["music"] : []) };
        var requests = 0;
        var resolver = CreateResolver(_ => { requests++; return Item("item-1"); }, access, source);

        Assert.Null(await resolver.ResolveAsync("item-1", Context(), default));
        Assert.Null(await resolver.ResolveArtworkAsync("item-1", Context(), default));
        Assert.Equal(0, source.RequestCount);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task MissingViewerCredentialNeverFallsBackToConfiguredAdministrator()
    {
        var source = new ViewerPermissionSource { Available = false };
        var requests = 0;
        var resolver = CreateResolver(_ => { requests++; return Item("item-1"); }, source: source);

        Assert.Null(await resolver.ResolveAsync("item-1", Context(), default));
        Assert.Null(await resolver.ResolveArtworkAsync("item-1", Context(), default));
        Assert.Equal(2, source.RequestCount);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task UnlinkedOrNonJellyfinContextCannotReadNativeMetadataOrArtwork()
    {
        var source = new ViewerPermissionSource();
        var requests = 0;
        var resolver = CreateResolver(_ => { requests++; return Item("item-1"); }, source: source);
        var unlinked = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "viewer", null,
            "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var subsonic = new ProtocolExecutionContext(ProtocolKind.Subsonic, "backend", "viewer",
            new AllstarrPrincipal(_tenantId, Guid.CreateVersion7(), "subsonic", "backend", "viewer", "Viewer", false),
            "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);

        foreach (var context in new[] { unlinked, subsonic })
        {
            Assert.Null(await resolver.ResolveAsync("item-1", context, default));
            Assert.Null(await resolver.ResolveArtworkAsync("item-1", context, default));
        }
        Assert.Equal(0, source.RequestCount);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ArtworkUsesFreshViewerItemAuthorizationAndAuthorizedAlbumArtworkId()
    {
        var requests = new List<string>();
        var source = new ViewerPermissionSource();
        var resolver = CreateResolver(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("viewer-token-viewer", request.Headers.GetValues("X-Emby-Token").Single());
            if (request.RequestUri.AbsolutePath == "/Items")
            {
                Assert.Equal("viewer", ParseQuery(request.RequestUri)["UserId"]);
                Assert.Equal("track-1", ParseQuery(request.RequestUri)["Ids"]);
                return Json("""
                    { "Items": [{ "Id": "track-1", "Name": "Album track", "AlbumId": "album-42",
                                  "AlbumPrimaryImageTag": "album-etag" }] }
                    """);
            }
            Assert.Equal("/Items/album-42/Images/Primary", request.RequestUri.AbsolutePath);
            return Image();
        }, source: source);

        var artwork = await resolver.ResolveArtworkAsync("track-1", Context(), default);

        Assert.NotNull(artwork);
        Assert.Equal("image/png", artwork.ContentType);
        Assert.Equal([1, 2, 3, 4], artwork.Content);
        Assert.Equal(["/Items", "/Items/album-42/Images/Primary"], requests);
        Assert.Equal(2, source.RequestCount);
    }

    [Fact]
    public async Task EarlierSuccessCannotServeMetadataOrArtworkAfterViewerItemAccessIsDenied()
    {
        var visible = true;
        var imageRequests = 0;
        var itemQueries = 0;
        var resolver = CreateResolver(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Items")
            {
                itemQueries++;
                return visible ? Item("item-1") : Json("""{ "Items": [] }""");
            }
            imageRequests++;
            return Image();
        });
        var context = Context();
        Assert.NotNull(await resolver.ResolveAsync("item-1", context, default));
        Assert.NotNull(await resolver.ResolveArtworkAsync("item-1", context, default));
        visible = false;

        Assert.Null(await resolver.ResolveAsync("item-1", context, default));
        Assert.Null(await resolver.ResolveArtworkAsync("item-1", context, default));
        Assert.Equal(4, itemQueries);
        Assert.Equal(1, imageRequests);
    }

    [Fact]
    public async Task DeniedSecondViewerCannotReuseFirstViewersMetadataOrArtwork()
    {
        var imageRequests = 0;
        var resolver = CreateResolver(request =>
        {
            var token = request.Headers.GetValues("X-Emby-Token").Single();
            if (request.RequestUri!.AbsolutePath == "/Items")
                return ParseQuery(request.RequestUri)["UserId"] == "viewer-a" ? Item("item-1") : Json("""{ "Items": [] }""");
            Assert.Equal("viewer-token-viewer-a", token);
            imageRequests++;
            return Image();
        });
        Assert.NotNull(await resolver.ResolveAsync("item-1", Context("viewer-a"), default));
        Assert.NotNull(await resolver.ResolveArtworkAsync("item-1", Context("viewer-a"), default));

        Assert.Null(await resolver.ResolveAsync("item-1", Context("viewer-b"), default));
        Assert.Null(await resolver.ResolveArtworkAsync("item-1", Context("viewer-b"), default));
        Assert.Equal(1, imageRequests);
    }

    [Theory]
    [InlineData("image/png", 5 * 1024 * 1024 + 1)]
    [InlineData("application/json", 4)]
    public async Task ArtworkRejectsUnboundedOrNonImageContent(string contentType, int contentLength)
    {
        var resolver = CreateResolver(request => request.RequestUri!.AbsolutePath == "/Items"
            ? Item("item-1") : Image(contentType, contentLength));

        Assert.Null(await resolver.ResolveArtworkAsync("item-1", Context(), default));
    }

    [Theory]
    [InlineData("{ \"Name\": \"Unscoped item\" }")]
    [InlineData("{ \"Items\": [{ \"Id\": \"different-item\", \"Name\": \"Other item\" }] }")]
    public async Task MetadataRequiresExactItemInUserFilteredCollection(string response)
    {
        var resolver = CreateResolver(_ => Json(response));

        Assert.Null(await resolver.ResolveAsync("item-1", Context(), default));
        Assert.Null(await resolver.ResolveArtworkAsync("item-1", Context(), default));
    }

    private JellyfinPlaybackMetadataResolver CreateResolver(Func<HttpRequestMessage, HttpResponseMessage> responder,
        ViewerLibraryAccess? access = null, ViewerPermissionSource? source = null) => new(
        new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(responder))),
        Options.Create(new JellyfinSettings { Url = "http://jellyfin.test", ApiKey = "configured-admin-key", UserId = "configured-admin" }),
        access ?? new ViewerLibraryAccess(), source ?? new ViewerPermissionSource(),
        NullLogger<JellyfinPlaybackMetadataResolver>.Instance);

    private ProtocolExecutionContext Context(string viewer = "viewer") => new(ProtocolKind.Jellyfin, "backend", viewer,
        new AllstarrPrincipal(_tenantId, Guid.CreateVersion7(), "jellyfin", "backend", viewer, "Viewer", false),
        "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private static HttpResponseMessage Item(string id, string title = "Fixture title") => Json(JsonSerializer.Serialize(new
    {
        Items = new[] { new
        {
            Id = id, Name = title, Artists = new[] { "Track artist" }, AlbumArtist = "Fixture artist", Album = "Fixture album",
            IndexNumber = 7, ProviderIds = new { MusicBrainzTrack = "11111111-1111-1111-1111-111111111111" },
            ImageTags = new { Primary = "etag-1" }
        } }
    }));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Image(string contentType = "image/png", int? contentLength = null)
    {
        var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.ContentType = new(contentType);
        if (contentLength.HasValue) content.Headers.ContentLength = contentLength;
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : string.Empty);

    private sealed class ViewerLibraryAccess : IBackendLibraryAccessResolver
    {
        public BackendLibraryAccess Access { get; set; } = new(true, ["music"]);
        public Task<BackendLibraryAccess> ResolveAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(Access);
        public Task<BackendLibraryAccessContext> ResolveUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task InvalidateAsync(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId) => Task.CompletedTask;
    }

    private sealed class ViewerPermissionSource : IBackendLibraryPermissionSource
    {
        public bool Available { get; set; } = true;
        public int RequestCount { get; private set; }
        public Task<HttpRequestMessage?> CreateRequestAsync(ProtocolExecutionContext context, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (!Available) return Task.FromResult<HttpRequestMessage?>(null);
            var request = new HttpRequestMessage(HttpMethod.Get, "http://jellyfin.test/UserViews");
            request.Headers.TryAddWithoutValidation("X-Emby-Token", $"viewer-token-{context.VerifiedBackendPrincipalId}");
            return Task.FromResult<HttpRequestMessage?>(request);
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
