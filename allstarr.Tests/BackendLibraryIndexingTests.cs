using System.Net;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public sealed class BackendLibraryIndexingTests
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();

    [Fact]
    public async Task JellyfinScanner_IndexesMetadataAndPathsWithoutReadingMedia()
    {
        var handler = new RecordingHandler("""
            {"Items":[
              {"Id":"song-1","Name":"First","Path":"/music/Artist/First.flac","Artists":["Artist"],"Album":"Album","AlbumArtist":"Artist","RunTimeTicks":1800000000,"DateModified":"2026-07-12T01:00:00Z","ProviderIds":{"Isrc":"USABC1234567","MusicBrainzTrack":"31e68c1d-31f9-432c-a3a4-13aef4a53833"},"ImageTags":{"Primary":"cover-v1"}},
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
        Assert.Equal("USABC1234567", track.Isrc);
        Assert.Equal("jellyfin-cover:song-1:cover-v1", track.CoverArtReference);
        Assert.Equal("ephemeral-key", handler.LastRequest!.Headers.GetValues("X-Emby-Token").Single());
        Assert.DoesNotContain("Audio", handler.LastRequest.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("Artist/Album/First.flac", Assert.Single(index.Inputs).FilePath);
    }

    private ProtocolExecutionContext Context(ProtocolKind protocol) => new(
        protocol,
        "primary",
        "principal",
        new AllstarrPrincipal(_tenant, _user, protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic", "primary", "principal", "Owner", false),
        "library-index-test",
        DateTimeOffset.UtcNow.AddMinutes(5),
        default,
        libraryScopeId: "music");

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
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingIndex : ILibraryIndexService
    {
        public List<LibraryTrackIndexInput> Inputs { get; } = [];

        public Task<IndexedLibraryTrack> UpsertAsync(ProtocolExecutionContext executionContext, LibraryTrackIndexInput input, CancellationToken cancellationToken = default)
        {
            Inputs.Add(input);
            return Task.FromResult(new IndexedLibraryTrack(
                Guid.CreateVersion7(), input.BackendItemId, input.FilePath, input.Title, input.Artist,
                input.Album, input.AlbumArtist, input.DurationMilliseconds, input.Isrc,
                input.MusicBrainzRecordingId, input.CanonicalRecordingId,
                input.ProviderTrackIds ?? new Dictionary<string, string>(), DateTimeOffset.UtcNow,
                input.SourceModifiedAt, 0));
        }

        public Task<IReadOnlyList<IndexedLibraryTrack>> ListAsync(ProtocolExecutionContext executionContext, string libraryScopeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IndexedLibraryTrack>>([]);

        public Task<IReadOnlyList<LocalTrackMatchCandidate>> GetMatchCandidatesAsync(ProtocolExecutionContext executionContext, string libraryScopeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalTrackMatchCandidate>>([]);
    }
}
