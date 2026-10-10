using System.Net;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class BackendLibraryIndexingTests
{
    private readonly Guid _user = Guid.CreateVersion7();

    public static TheoryData<ProtocolKind> BothProtocols => new() { ProtocolKind.Jellyfin, ProtocolKind.Subsonic };

    [Theory]
    [MemberData(nameof(BothProtocols))]
    public async Task Scanner_NoExplicitTargetIndexesEveryDiscoveredLibrary(ProtocolKind protocol)
    {
        var handler = new RecordingHandler(CatalogWithTrack(protocol), Discovery(protocol, "lib-a", "lib-b"));
        var index = new RecordingIndex();
        var scanner = CreateScanner(protocol, handler, index);

        var result = await scanner.ScanAsync(Context(protocol), new(null, protocol == ProtocolKind.Subsonic ? Guid.CreateVersion7() : null, 50), default);

        Assert.Equal(2, result.Pages);
        Assert.Equal(new[] { "lib-a", "lib-b" }, index.Inputs.Select(track => track.BackendLibraryId).ToArray());
        Assert.Equal(new[] { _user, _user }, index.ContextOwners);
        var pages = handler.Requests.Where(request => IsCatalogRequest(request.Uri)).ToArray();
        Assert.Equal(2, pages.Length);
        foreach (var library in new[] { "lib-a", "lib-b" })
        {
            var page = Assert.Single(pages, request => HasLibraryParameter(request, protocol, library));
            Assert.NotNull(page);
        }
        if (protocol == ProtocolKind.Jellyfin)
        {
            Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/UserViews" &&
                request.Uri.Query.Contains("IncludeHidden=true", StringComparison.Ordinal));
            Assert.All(handler.Requests, request =>
                Assert.Contains("UserId=principal", request.Uri.Query, StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath == "/Library/MediaFolders");
        }
    }

    [Theory]
    [MemberData(nameof(BothProtocols))]
    public async Task Scanner_ConfiguredSubsetIndexesOnlySelectedLibrary(ProtocolKind protocol)
    {
        var handler = new RecordingHandler(CatalogWithTrack(protocol), Discovery(protocol, "lib-a", "lib-b"));
        var index = new RecordingIndex();
        var scanner = CreateScanner(protocol, handler, index,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [BackendMusicLibraries.SelectionKey] = "lib-b" }).Build());

        var result = await scanner.ScanAsync(Context(protocol), new(null, protocol == ProtocolKind.Subsonic ? Guid.CreateVersion7() : null, 50), default);

        Assert.Equal(1, result.Pages);
        Assert.Equal(new[] { "lib-b" }, index.Inputs.Select(track => track.BackendLibraryId).ToArray());
        Assert.Equal(new[] { _user }, index.ContextOwners);
        Assert.Single(handler.Requests, request => IsCatalogRequest(request.Uri));
        Assert.Contains(handler.Requests, request => IsCatalogRequest(request.Uri) && HasLibraryParameter(request, protocol, "lib-b"));
    }

    [Theory]
    [MemberData(nameof(BothProtocols))]
    public async Task Scanner_RejectsExplicitTargetOutsideDiscoveredSelection(ProtocolKind protocol)
    {
        var handler = new RecordingHandler("{}", Discovery(protocol, "lib-a", "lib-b"));
        var index = new RecordingIndex();
        var scanner = CreateScanner(protocol, handler, index,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [BackendMusicLibraries.SelectionKey] = "lib-a" }).Build());

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync(Context(protocol), new("lib-b", protocol == ProtocolKind.Subsonic ? Guid.CreateVersion7() : null), default));

        Assert.Empty(index.Inputs);
        Assert.DoesNotContain(handler.Requests, request => IsCatalogRequest(request.Uri));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(BothProtocols))]
    public async Task Scanner_RejectsMalformedLibraryPermissionResponse(ProtocolKind protocol)
    {
        var handler = new RecordingHandler("{}", "{}");
        var index = new RecordingIndex();
        var scanner = CreateScanner(protocol, handler, index);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync(Context(protocol), new(null, protocol == ProtocolKind.Subsonic ? Guid.CreateVersion7() : null), default));

        Assert.Empty(index.Inputs);
        Assert.DoesNotContain(handler.Requests, request => IsCatalogRequest(request.Uri));
        Assert.Single(handler.Requests);
    }

    private IBackendLibraryCatalogScanner CreateScanner(ProtocolKind protocol, RecordingHandler handler, RecordingIndex index, IConfiguration? configuration = null) =>
        protocol == ProtocolKind.Jellyfin
            ? new JellyfinLibraryCatalogScanner(new HttpClient(handler), new JellyfinSettings { Url = "https://jellyfin.test", ApiKey = "ephemeral-key" }, index, new Clock(), configuration)
            : new SubsonicLibraryCatalogScanner(new HttpClient(handler), new SubsonicSettings { Url = "https://navidrome.test" }, new AuthenticationResolver(), index, new Clock(), configuration);

    private static string Discovery(ProtocolKind protocol, params string[] ids) => protocol == ProtocolKind.Jellyfin
        ? "{\"Items\":[" + string.Join(',', ids.Select(id => "{\"Id\":\"" + id + "\",\"CollectionType\":\"music\"}")) + "]}"
        : "{\"subsonic-response\":{\"status\":\"ok\",\"musicFolders\":{\"musicFolder\":[" +
          string.Join(',', ids.Select(id => "{\"id\":\"" + id + "\"}")) + "]}}}";

    private static string CatalogWithTrack(ProtocolKind protocol) => protocol == ProtocolKind.Jellyfin
        ? "{\"Items\":[{\"Id\":\"song\",\"Name\":\"Song\",\"Path\":\"/music/song.flac\",\"Artists\":[\"Artist\"],\"DateCreated\":\"2026-07-12T01:00:00Z\"}],\"TotalRecordCount\":1}"
        : "{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{\"song\":[{\"id\":\"song\",\"title\":\"Song\",\"artist\":\"Artist\",\"path\":\"song.flac\",\"created\":\"2026-07-12T01:00:00Z\"}]}}}";

    private static bool IsCatalogRequest(Uri uri) => uri.AbsolutePath.EndsWith("Items", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith("search3.view", StringComparison.Ordinal);
    private static bool HasLibraryParameter(CapturedRequest request, ProtocolKind protocol, string id) => protocol == ProtocolKind.Jellyfin
        ? Uri.UnescapeDataString(request.Uri.Query).Contains("ParentId=" + id, StringComparison.Ordinal)
        : request.Body.Contains("musicFolderId=" + id, StringComparison.Ordinal);

    [Fact]
    public async Task JellyfinScanner_IndexesMetadataAndPathsWithoutReadingMedia()
    {
        var handler = new RecordingHandler("""
            {"Items":[
              {"Id":"song-1","Name":"First","Path":"/music/Artist/First.flac","Artists":["Artist","Featured Artist"],"Album":"Album","AlbumArtist":"Artist","RunTimeTicks":1800000000,"DateModified":"2026-07-12T01:00:00Z","ProviderIds":{"Isrc":"USABC1234567","MusicBrainzRecording":"16ba7915-2acf-42b2-8c87-ed67090dca91","MusicBrainzTrack":"31e68c1d-31f9-432c-a3a4-13aef4a53833"},"ImageTags":{"Primary":"cover-v1"}},
              {"Id":"song-2","Name":"Pathless","Artists":["Artist"],"DateModified":"2026-07-12T01:00:00Z"}
            ],"TotalRecordCount":2}
            """);
        var index = new RecordingIndex();
        var scanner = new JellyfinLibraryCatalogScanner(
            new HttpClient(handler),
            new JellyfinSettings { Url = "https://jellyfin.test", ApiKey = "ephemeral-key" },
            index,
            new Clock());

        var result = await scanner.ScanAsync(Context(ProtocolKind.Jellyfin), new("music", PageSize: 50), default);

        Assert.Equal(new LibraryCatalogScanResult(2, 1, 1, 0, 1), result);
        var track = Assert.Single(index.Inputs);
        Assert.Equal("/music/Artist/First.flac", track.FilePath);
        Assert.Equal("Artist, Featured Artist", track.Artist);
        Assert.Equal(180_000, track.DurationMilliseconds);
        Assert.Equal("jellyfin", track.DurationProvenance);
        Assert.Equal(new Clock().UtcNow, track.DurationRetrievedAt);
        Assert.Equal("USABC1234567", track.Isrc);
        Assert.Equal("16ba7915-2acf-42b2-8c87-ed67090dca91", track.MusicBrainzRecordingId);
        Assert.Equal("jellyfin-cover:song-1:cover-v1", track.CoverArtReference);
        Assert.Contains("Token=\"ephemeral-key\"", handler.LastRequest!.Headers.GetValues("Authorization").Single());
        Assert.False(handler.LastRequest.Headers.Contains("X-Emby-Token"));
        Assert.DoesNotContain("Audio", handler.LastRequest.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "IncludeItemTypes=Audio",
            Uri.UnescapeDataString(handler.LastRequest.RequestUri.Query),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ParentId=music", Uri.UnescapeDataString(handler.LastRequest.RequestUri.Query), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JellyfinScanner_FallsBackToDateCreatedWhenDateModifiedIsAbsent()
    {
        var handler = new RecordingHandler("""
            {"Items":[
              {"Id":"song-1","Name":"First","Path":"/music/Artist/First.flac","Artists":["Artist"],"RunTimeTicks":1800000000,"DateCreated":"2026-07-12T01:00:00Z"}
            ],"TotalRecordCount":1}
            """);
        var index = new RecordingIndex();
        var scanner = new JellyfinLibraryCatalogScanner(
            new HttpClient(handler),
            new JellyfinSettings { Url = "https://jellyfin.test", ApiKey = "ephemeral-key" },
            index,
            new Clock());

        var result = await scanner.ScanAsync(Context(ProtocolKind.Jellyfin), new("music", PageSize: 50), default);

        Assert.Equal(1, result.Indexed);
        Assert.Single(index.Inputs);
        Assert.Contains("DateCreated", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubsonicScanner_PostsEphemeralCredentialsAndReportsMalformedEntries()
    {
        var handler = new RecordingHandler("""
            {"subsonic-response":{"status":"ok","searchResult3":{"song":[
              {"id":"song-1","title":"First","artist":"Artist","album":"Album","path":"Artist/Album/First.flac","duration":180,"created":"2026-07-12T01:00:00Z","isrc":"USABC1234567","coverArt":"cover-1"},
              {"id":"song-2","title":"No date","artist":"Artist","path":"Artist/No date.flac"}
            ]}}}
            """);
        var index = new RecordingIndex();
        var credential = Guid.CreateVersion7();
        var scanner = new SubsonicLibraryCatalogScanner(
            new HttpClient(handler),
            new SubsonicSettings { Url = "https://navidrome.test" },
            new AuthenticationResolver(),
            index,
            new Clock());

        var result = await scanner.ScanAsync(
            Context(ProtocolKind.Subsonic),
            new("music", credential, 50),
            default);

        Assert.Equal(new LibraryCatalogScanResult(2, 1, 0, 1, 1), result);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.DoesNotContain("password", handler.LastRequest.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("p=password", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("musicFolderId=music", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal("Artist/Album/First.flac", Assert.Single(index.Inputs).FilePath);
        Assert.Equal(180_000, index.Inputs[0].DurationMilliseconds);
        Assert.Equal("subsonic", index.Inputs[0].DurationProvenance);
    }

    [Fact]
    public async Task JellyfinScanner_PreservesMissingDurationAsUnknown()
    {
        var handler = new RecordingHandler("""
            {"Items":[
              {"Id":"song-1","Name":"First","Path":"/music/First.flac","Artists":["Artist"],"DateCreated":"2026-07-12T01:00:00Z"}
            ],"TotalRecordCount":1}
            """);
        var index = new RecordingIndex();
        var scanner = new JellyfinLibraryCatalogScanner(
            new HttpClient(handler),
            new JellyfinSettings { Url = "https://jellyfin.test", ApiKey = "ephemeral-key" },
            index,
            new Clock());

        await scanner.ScanAsync(Context(ProtocolKind.Jellyfin), new("music", PageSize: 50), default);

        var track = Assert.Single(index.Inputs);
        Assert.Null(track.DurationMilliseconds);
        Assert.Null(track.DurationProvenance);
        Assert.Null(track.DurationRetrievedAt);
    }

    private ProtocolExecutionContext Context(ProtocolKind protocol) => new(
        protocol,
        "primary",
        "principal",
        new AllstarrPrincipal(_user, protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic", "primary", "principal", "Owner", false),
        "library-index-test",
        DateTimeOffset.UtcNow.AddMinutes(5),
        default);

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 12, 2, 0, 0, TimeSpan.Zero);
    }

    private sealed class AuthenticationResolver : IBackendPlaylistAuthenticationResolver
    {
        public ValueTask<BackendPlaylistAuthentication> ResolveAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BackendPlaylistAuthentication(
                new Dictionary<string, string>(),
                [new("u", "user"), new("p", "password")]));
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        private readonly string? _discoveryBody = null;
        public RecordingHandler(string responseBody, string discoveryBody) : this(responseBody) => _discoveryBody = discoveryBody;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.RequestUri!, LastBody));
            var body = _discoveryBody ?? (request.RequestUri!.AbsolutePath.EndsWith("UserViews", StringComparison.Ordinal)
                ? "{\"Items\":[{\"Id\":\"music\",\"CollectionType\":\"music\"}]}"
                : request.RequestUri.AbsolutePath.EndsWith("getMusicFolders.view", StringComparison.Ordinal)
                    ? "{\"subsonic-response\":{\"status\":\"ok\",\"musicFolders\":{\"musicFolder\":[{\"id\":\"music\"}]}}}"
                    : responseBody);
            if (request.RequestUri!.AbsolutePath.EndsWith("Items", StringComparison.Ordinal) || request.RequestUri.AbsolutePath.EndsWith("search3.view", StringComparison.Ordinal)) body = responseBody;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record CapturedRequest(Uri Uri, string Body);

    private sealed class RecordingIndex : ILibraryIndexService
    {
        public List<LibraryTrackIndexInput> Inputs { get; } = [];
        public List<Guid> ContextOwners { get; } = [];

        public Task<IndexedLibraryTrack> UpsertAsync(ProtocolExecutionContext executionContext, LibraryTrackIndexInput input, CancellationToken cancellationToken = default)
        {
            Inputs.Add(input);
            ContextOwners.Add(executionContext.Principal!.UserId);
            return Task.FromResult(new IndexedLibraryTrack(
                Guid.CreateVersion7(), input.BackendItemId, input.FilePath, input.Title, input.Artist,
                input.Album, input.AlbumArtist, input.DurationMilliseconds,
                input.DurationProvenance, input.DurationRetrievedAt, input.Isrc,
                input.MusicBrainzRecordingId, input.CanonicalRecordingId,
                input.ProviderTrackIds ?? new Dictionary<string, string>(), DateTimeOffset.UtcNow,
                input.SourceModifiedAt, 0));
        }

        public Task<IReadOnlyList<IndexedLibraryTrack>> ListAsync(ProtocolExecutionContext executionContext, string backendLibraryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IndexedLibraryTrack>>([]);

        public Task<IReadOnlyList<LocalTrackMatchCandidate>> GetMatchCandidatesAsync(ProtocolExecutionContext executionContext, string backendLibraryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalTrackMatchCandidate>>([]);

        public Task RecordScanSummaryAsync(ProtocolExecutionContext executionContext, LibraryCatalogScanResult result, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
