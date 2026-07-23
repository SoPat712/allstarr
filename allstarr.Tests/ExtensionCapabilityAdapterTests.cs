using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Downloads;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace allstarr.Tests;

public sealed class ExtensionCapabilityAdapterTests
{
    [Fact]
    public void RepeatedExtensionRuntimeErrors_AreSanitizedAndDeduplicated()
    {
        const string manifest = """
            {"id":"logging-demo","displayName":"Logging demo","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
             "capabilities":[{"kind":"Metadata","hooks":["searchTracks"],"accountScopes":[],"accountRequired":false}],
             "permissions":[]}
            """;
        const string script = """
            registerExtension({
              searchTracks:function(){
                log.error('<redacted>');
                return {items:[]};
              }
            });
            """;
        var events = new List<(string Level, string Message)>();
        var permissions = new ExtensionRuntimePermissionSet(
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            LogSink: (level, message) => events.Add((level, message)));
        var firstSandbox = new ExtensionSandbox(
            Path.GetTempPath(),
            manifest,
            script,
            new HttpClientFactory(),
            NullLogger.Instance,
            permissions);
        var recreatedSandbox = new ExtensionSandbox(
            Path.GetTempPath(),
            manifest,
            script,
            new HttpClientFactory(),
            NullLogger.Instance,
            permissions);

        firstSandbox.InvokeJson("searchTracks", "{}");
        recreatedSandbox.InvokeJson("searchTracks", "{}");
        recreatedSandbox.InvokeJson("searchTracks", "{}");

        var runtimeEvent = Assert.Single(events);
        Assert.Equal("error", runtimeEvent.Level);
        Assert.Equal(
            "Provider operation failed without a safe diagnostic.",
            runtimeEvent.Message);
        Assert.DoesNotContain("redacted", runtimeEvent.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepeatedExtensionHttpFailures_OpenCooldownAndSuppressNetworkCalls()
    {
        const string manifest = """
            {"id":"failure-demo","displayName":"Failure demo","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
             "capabilities":[{"kind":"Metadata","hooks":["searchTracks"],"accountScopes":[],"accountRequired":false}],
             "permissions":[{"kind":"Network","value":"https://api.example.test/","required":true}]}
            """;
        const string script = """
            registerExtension({
              searchTracks:function(){
                var response = http.get('https://api.example.test/search', {});
                return {items:[], statusCode:response.statusCode, error:response.error || null};
              }
            });
            """;
        var handler = new StatusHandler(HttpStatusCode.Forbidden);
        var permissions = new ExtensionRuntimePermissionSet(
            new HashSet<string>(["https://api.example.test/"]),
            new HashSet<string>(),
            new HashSet<string>());
        var sandbox = new ExtensionSandbox(
            Path.GetTempPath(),
            manifest,
            script,
            new HttpClientFactory(handler),
            NullLogger.Instance,
            permissions);

        var first = sandbox.InvokeJson("searchTracks", "{}");
        var second = sandbox.InvokeJson("searchTracks", "{}");
        var suppressed = sandbox.InvokeJson("searchTracks", "{}");

        Assert.Contains("\"statusCode\":403", first, StringComparison.Ordinal);
        Assert.Contains("\"statusCode\":403", second, StringComparison.Ordinal);
        Assert.Contains("\"statusCode\":503", suppressed, StringComparison.Ordinal);
        Assert.Contains("provider_temporarily_unavailable", suppressed, StringComparison.Ordinal);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public void SuccessfulExtensionHttpCall_ResetsConsecutiveFailureCount()
    {
        const string manifest = """
            {"id":"recovery-demo","displayName":"Recovery demo","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
             "capabilities":[{"kind":"Metadata","hooks":["searchTracks"],"accountScopes":[],"accountRequired":false}],
             "permissions":[{"kind":"Network","value":"https://api.example.test/","required":true}]}
            """;
        const string script = """
            registerExtension({
              searchTracks:function(){
                var response = http.get('https://api.example.test/search', {});
                return {items:[], statusCode:response.statusCode, error:response.error || null};
              }
            });
            """;
        var handler = new StatusHandler(
            HttpStatusCode.Forbidden,
            HttpStatusCode.OK,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden);
        var permissions = new ExtensionRuntimePermissionSet(
            new HashSet<string>(["https://api.example.test/"]),
            new HashSet<string>(),
            new HashSet<string>());
        var sandbox = new ExtensionSandbox(
            Path.GetTempPath(),
            manifest,
            script,
            new HttpClientFactory(handler),
            NullLogger.Instance,
            permissions);

        sandbox.InvokeJson("searchTracks", "{}");
        sandbox.InvokeJson("searchTracks", "{}");
        sandbox.InvokeJson("searchTracks", "{}");
        sandbox.InvokeJson("searchTracks", "{}");
        var suppressed = sandbox.InvokeJson("searchTracks", "{}");

        Assert.Contains("provider_temporarily_unavailable", suppressed, StringComparison.Ordinal);
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public void SignedSessionRuntime_BootstrapsExchangesAndSignsRequests()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-signed-session", Guid.NewGuid().ToString("N"));
        try
        {
            const string manifest = """
                {"id":"signed-demo","displayName":"Signed demo","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
                 "capabilities":[{"kind":"Metadata","hooks":["searchTracks"],"accountScopes":[],"accountRequired":false}],
                 "permissions":[{"kind":"Network","value":"https://api.example.test/","required":true}],
                 "requiredRuntimeFeatures":["signedSession@1","sessionGrant@1"],
                 "signedSession":{"namespace":"demo-v1","baseUrl":"https://api.example.test/v1","appVersion":"demo@1.0.0"}}
                """;
            const string script = """
                registerExtension({
                  searchTracks:function(){return session.signedFetch('GET','/catalog',null,{});},
                  authorize:function(){return session.signedFetch('GET','/catalog',null,{});}
                });
                """;
            var handler = new SignedSessionHandler();
            var permissions = new ExtensionRuntimePermissionSet(
                new HashSet<string>(["https://api.example.test/"]), new HashSet<string>(), new HashSet<string>());
            var sandbox = new ExtensionSandbox(root, manifest, script, new HttpClientFactory(handler), NullLogger.Instance,
                permissions, Path.Combine(root, "runtime"), new EphemeralDataProtectionProvider().CreateProtector("test"));

            var verification = sandbox.InvokeJson("authorize", "{}");
            Assert.True(verification?.Contains("VERIFY_REQUIRED", StringComparison.Ordinal) == true, verification);
            Assert.Contains("challenge-1", verification, StringComparison.Ordinal);

            var exchange = JsonSerializer.Serialize(sandbox.CompleteSignedSessionGrant(
                "spotiflac://session-grant/?cb_version=v2grant&state=signed-demo&grant=grant-1"));
            Assert.Contains("true", exchange, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("grant-1", handler.ExchangeGrant);

            var response = sandbox.InvokeJson("searchTracks", "{}");
            Assert.Contains("catalog ok", response, StringComparison.Ordinal);
            Assert.NotNull(handler.SignedRequest);
            Assert.Equal("session-1", handler.SignedRequest!.Headers.GetValues("X-Sig-Session").Single());
            Assert.True(handler.SignedRequest.Headers.Contains("X-Sig-Signature"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SpotiFlacRuntimeAdapter_MapsLegacySearchAndArtwork()
    {
        const string sourceManifest = """
            {"name":"demo","displayName":"Demo","version":"1.0.0","description":"Fixture",
             "type":["metadata_provider"],"permissions":{"storage":true}}
            """;
        const string script = """
            registerExtension({customSearch:function(){return [{id:'track-1',name:'Song',artists:['Artist'],album_name:'Album',cover_url:'https://images.example.test/cover.jpg',item_type:'track'}];},getPlaylist:function(){return {tracks:[]};}});
            """;
        var manifest = SpotiFlacExtensionCompatibility.NormalizeManifest(sourceManifest, script);
        Assert.DoesNotContain(ExtensionSdkV1.ParseManifest(manifest).Capabilities,
            capability => capability.Kind == ProviderCapabilityKind.Playlist);
        var permissions = new ExtensionRuntimePermissionSet(new HashSet<string>(), new HashSet<string>(["*"]), new HashSet<string>());
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), manifest, script,
            new HttpClientFactory(), NullLogger.Instance, permissions);

        var json = sandbox.InvokeJson("searchTracks", "{\"query\":\"Song\",\"page\":{\"limit\":10}}");

        Assert.NotNull(json);
        Assert.Contains("\"title\":\"Song\"", json, StringComparison.Ordinal);
        Assert.Contains("https://images.example.test/cover.jpg", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpotiFlacRuntimeAdapter_UsesKnownTrackMetadataForTimedLyrics()
    {
        const string sourceManifest = """
            {"name":"lyrics-demo","displayName":"Lyrics demo","version":"1.0.0","description":"Fixture",
             "type":["lyrics_provider"],"permissions":{}}
            """;
        const string script = """
            registerExtension({
              getTrack:function(){throw new Error('metadata lookup must not run');},
              fetchLyrics:function(title,artist,album,duration){
                if(title!=='Known title'||artist!=='Known artist'||album!=='Known album'||duration!==240) throw new Error('wrong metadata');
                return {provider:'fixture',plainLyrics:'line one\nline two',lines:[
                  {startTimeMs:1250,words:'line one'},
                  {startTimeMs:62500,words:'line two'},
                  {startTimeMs:999999999,words:''}
                ]};
              }
            });
            """;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(sourceManifest, script);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), normalized, script,
            new HttpClientFactory(), NullLogger.Instance, ExtensionRuntimePermissionSet.None);
        var adapter = new ExtensionLyricsCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.FetchLyricsAsync(Context("spotiflac-lyrics-demo"), new ProviderLyricsRequest(
            Guid.CreateVersion7(),
            new ProviderExternalResourceId("spotiflac-lyrics-demo", ProviderResourceKind.Track, "catalog-id"),
            preferredFormat: ProviderLyricsFormat.LineTimed,
            trackTitle: "Known title",
            artistNames: ["Known artist"],
            albumTitle: "Known album",
            durationSeconds: 240));

        Assert.True(outcome.IsSuccess, outcome.Error?.Kind.ToString());
        Assert.Equal(ProviderLyricsFormat.LineTimed, outcome.Value!.Format);
        Assert.Equal("[00:01.25]line one\n[01:02.50]line two", outcome.Value.Content);
    }

    [Fact]
    public async Task SpotiFlacRuntimeAdapter_BrokersDirectDownloadsIntoManagedWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-spotiflac-download", Guid.NewGuid().ToString("N"));
        try
        {
            const string sourceManifest = """
                {"name":"demo","displayName":"Demo","version":"1.0.0","description":"Fixture",
                 "type":["metadata_provider","download_provider"],"permissions":{"network":["media.example.test"]}}
                """;
            const string script = """
                registerExtension({customSearch:function(){return [];},checkAvailability:function(){return {available:true};},
                  download:function(id, quality, path){return file.download('https://media.example.test/audio', path, {});}});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(sourceManifest, script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var permissions = new ExtensionRuntimePermissionSet(new HashSet<string>(["https://media.example.test/"]), new HashSet<string>(), new HashSet<string>());
            var sandbox = new ExtensionSandbox(root, normalized, script,
                new HttpClientFactory(new BytesHandler([1, 2, 3, 4])), NullLogger.Instance, permissions, Path.Combine(root, "runtime"));
            var store = new MemoryArtifactStore();
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 1024 };
            var resolver = new ProviderDownloadArtifactResolver(store, options);
            var adapter = new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options);
            var context = Context("spotiflac-demo");
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(
                context.Actor.TenantId, context.Actor.EffectiveUserId, job, "spotiflac-demo", null, "download"));

            string? raw;
            using (ExtensionArtifactInvocationScope.Open(resolver, workspace.Reference, job, "spotiflac-demo", 1024, CancellationToken.None))
                raw = sandbox.InvokeJson("download", "{\"trackId\":\"track-1\",\"requestedQuality\":\"Any\"}");

            Assert.NotNull(raw);
            Assert.Contains("\"sizeBytes\":4", raw, StringComparison.Ordinal);
            var outcome = await adapter.DownloadAsync(context, new(
                new ProviderExternalResourceId("spotiflac-demo", ProviderResourceKind.Track, "track-1"),
                job, workspace.Reference, ProviderAudioQuality.Any));
            Assert.True(outcome.IsSuccess, outcome.Error?.Kind.ToString());
            Assert.Equal(4, outcome.Value!.SizeBytes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Metadata_AllDeclaredAlbumAndArtistHooksMapTypedSchemas()
    {
        string[] hooks = ["searchTracks", "getTrack", "lookupByIsrc", "searchAlbums", "getAlbum", "searchArtists", "getArtist"];
        var manifest = Manifest(ProviderCapabilityKind.Metadata, hooks);
        var sandbox = Sandbox(manifest, """
            const artist = { id: 'artist-1', name: 'Artist', artworkUrl: 'https://img.example/artist.jpg', snapshotVersion: 'a1' };
            const album = { id: 'album-1', title: 'Album', artists: [{ id: 'artist-1', name: 'Artist' }], trackCount: 8, snapshotVersion: 'b1' };
            const track = { id: 'track-1', title: 'Track', artists: [{ id: 'artist-1', name: 'Artist' }], albumId: 'album-1', albumTitle: 'Album', durationMs: 1234, isrc: 'USABC1234567' };
            registerExtension({
              searchTracks: function() { return { items: [track] }; }, getTrack: function() { return track; }, lookupByIsrc: function() { return track; },
              searchAlbums: function() { return { items: [album], nextCursor: 'next' }; }, getAlbum: function() { return album; },
              searchArtists: function() { return { items: [artist] }; }, getArtist: function() { return artist; }
            });
            """);
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var search = new ProviderMetadataSearchRequest("fixture", new ProviderPageRequest(10));

        Assert.Equal("album-1", Assert.Single((await adapter.SearchAlbumsAsync(Context(), search)).RequireValue().Items).Id.Value);
        Assert.Equal("next", (await adapter.SearchAlbumsAsync(Context(), search)).RequireValue().NextCursor);
        Assert.Equal(8, (await adapter.GetAlbumAsync(Context(), new ProviderAlbumLookupRequest(Id(ProviderResourceKind.Album, "album-1")))).RequireValue().TrackCount);
        Assert.Equal("artist-1", Assert.Single((await adapter.SearchArtistsAsync(Context(), search)).RequireValue().Items).Id.Value);
        Assert.Equal("Artist", (await adapter.GetArtistAsync(Context(), new ProviderArtistLookupRequest(Id(ProviderResourceKind.Artist, "artist-1")))).RequireValue().Name);
        Assert.Equal("USABC1234567", (await adapter.LookupByIsrcAsync(Context(), new ProviderIsrcLookupRequest("USABC1234567"))).RequireValue().Isrc);
    }

    [Fact]
    public async Task SharedSandbox_SerializesMetadataAndHealthCapabilityCalls()
    {
        var manifest = new ExtensionSdkManifest("fixture-extension", "Fixture", "1.0.0", "1", "index.js",
        [
            new ExtensionSdkCapability(ProviderCapabilityKind.Metadata, ["searchArtists"], [ProviderAccountScope.User]),
            new ExtensionSdkCapability(ProviderCapabilityKind.Health, ["probeMetadata"], [ProviderAccountScope.User])
        ], []);
        var sandbox = Sandbox(manifest, """
            let active = false;
            function enter(value) { if (active) throw new Error('concurrent engine access'); active = true; for (let i = 0; i < 25000; i++) {} active = false; return value; }
            registerExtension({
              searchArtists: function() { return enter({ items: [{ id: 'artist-1', name: 'Artist' }] }); },
              probeMetadata: function() { return enter({ status: 'Healthy', observedAt: '2030-01-01T00:00:00Z', latencyMs: 1 }); }
            });
            """);
        var metadata = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var health = new ExtensionHealthCapabilityAdapter(sandbox, manifest);

        var calls = Enumerable.Range(0, 20).SelectMany(_ => new Task<bool>[]
        {
            Task.Run(async () => (await metadata.SearchArtistsAsync(Context(), new ProviderMetadataSearchRequest("x", new ProviderPageRequest(1)))).IsSuccess),
            Task.Run(async () => (await health.ProbeAsync(Context(), new ProviderHealthProbeRequest(ProviderCapabilityKind.Metadata))).IsSuccess)
        });

        Assert.All(await Task.WhenAll(calls), Assert.True);
    }

    [Fact]
    public async Task Streaming_MapsOnlyTypedLeaseAndRejectsFilesystemSource()
    {
        var manifest = Manifest(ProviderCapabilityKind.Streaming, "getStreamLease");
        var sandbox = Sandbox(manifest, """
            registerExtension({ getStreamLease: function(request) { return {
              leaseId: 'lease-1', sourceUri: 'file:///etc/passwd', expiresAt: '2030-01-01T00:00:00Z',
              supportsByteRanges: true, supportsSeeking: true,
              media: { mimeType: 'audio/flac', container: 'flac', codec: 'flac' }, retryBehavior: 'RefreshLease'
            }; }});
            """);
        var adapter = new ExtensionStreamingCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.GetStreamLeaseAsync(Context(),
            new ProviderStreamLeaseRequest(Id(ProviderResourceKind.Track, "track-1")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
    }

    [Fact]
    public async Task Lyrics_RejectsOversizedProviderContentWithoutEchoingIt()
    {
        var manifest = Manifest(ProviderCapabilityKind.Lyrics, "fetchLyrics");
        var sandbox = Sandbox(manifest, """
            registerExtension({ fetchLyrics: function(request) { return {
              availability: 'Available', source: 'fixture', format: 'PlainText', content: 'x'.repeat(2000001)
            }; }});
            """);
        var adapter = new ExtensionLyricsCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.FetchLyricsAsync(Context(), new ProviderLyricsRequest(
            Guid.CreateVersion7(), Id(ProviderResourceKind.Track, "track-1")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
        Assert.DoesNotContain("x", outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Playlist_ReadRequiresSelectedAccountBeforeInvokingExtension()
    {
        var manifest = Manifest(ProviderCapabilityKind.Playlist, "getUserPlaylists");
        var sandbox = Sandbox(manifest, """
            registerExtension({ getUserPlaylists: function(request) { throw new Error('secret provider diagnostic'); } });
            """);
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.GetUserPlaylistsAsync(Context(),
            new ProviderUserPlaylistsRequest(new ProviderPageRequest()));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, outcome.Error!.Kind);
        Assert.DoesNotContain("diagnostic", outcome.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForeignResourceId_IsRejectedBeforeHookInvocation()
    {
        var manifest = Manifest(ProviderCapabilityKind.Download, "checkAvailability");
        var sandbox = Sandbox(manifest, "registerExtension({ checkAvailability: function() { return { state: 'Available' }; } });");
        var adapter = new ExtensionDownloadCapabilityAdapter(sandbox, manifest);

        await Assert.ThrowsAsync<ArgumentException>(() => adapter.CheckAvailabilityAsync(Context(),
            new ProviderDownloadAvailabilityRequest(new ProviderExternalResourceId("other-provider", ProviderResourceKind.Track, "track-1"))));
    }

    [Fact]
    public async Task Download_BrokerStreamsIntoExactWorkspaceAndRetryReusesVerifiedArtifact()
    {
        var bytes = Encoding.UTF8.GetBytes("extension audio");
        using var fixture = DownloadFixture(bytes, 1024, "track.flac");

        var first = await fixture.Adapter.DownloadAsync(fixture.Context,
            new(fixture.Track, fixture.JobId, fixture.Workspace.Reference, ProviderAudioQuality.Lossless));
        var second = await fixture.Adapter.DownloadAsync(fixture.Context,
            new(fixture.Track, fixture.JobId, fixture.Workspace.Reference, ProviderAudioQuality.Lossless));

        var output = first.RequireValue();
        Assert.Equal("track.flac", output.ArtifactId);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), output.Sha256);
        Assert.Equal(output, second.RequireValue());
        var resolved = await fixture.Resolver.ResolveAsync(fixture.Workspace.Reference, output);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(resolved.SourcePath));
        Assert.Single(fixture.Store.Artifacts);
    }

    [Fact]
    public async Task Download_BrokerRejectsOversizeInvalidPathAndForeignWorkspaceLineage()
    {
        using var oversized = DownloadFixture(new byte[128], 16, "track.flac");
        Assert.Equal(ProviderErrorKind.TransientFailure,
            (await oversized.Adapter.DownloadAsync(oversized.Context,
                new(oversized.Track, oversized.JobId, oversized.Workspace.Reference, ProviderAudioQuality.Any))).Error!.Kind);

        using var traversal = DownloadFixture(Encoding.UTF8.GetBytes("audio"), 1024, "../track.flac");
        Assert.Equal(ProviderErrorKind.TransientFailure,
            (await traversal.Adapter.DownloadAsync(traversal.Context,
                new(traversal.Track, traversal.JobId, traversal.Workspace.Reference, ProviderAudioQuality.Any))).Error!.Kind);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(traversal.Root, traversal.Workspace.Reference.WorkspaceId)));

        using var foreign = DownloadFixture(Encoding.UTF8.GetBytes("audio"), 1024, "track.flac");
        var otherJob = Guid.CreateVersion7();
        var foreignWorkspace = await foreign.Resolver.CreateWorkspaceAsync(new(
            foreign.Context.Actor.TenantId, foreign.Context.Actor.EffectiveUserId, otherJob,
            "fixture-extension", null, "foreign"));
        Assert.Equal(ProviderErrorKind.TransientFailure,
            (await foreign.Adapter.DownloadAsync(foreign.Context,
                new(foreign.Track, foreign.JobId, foreignWorkspace.Reference, ProviderAudioQuality.Any))).Error!.Kind);
    }

    [Fact]
    public async Task Download_RejectsClaimThatWasNotWrittenThroughBroker()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-extension-claim", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemoryArtifactStore();
            var options = new ProviderDownloadWorkspaceOptions { RootPath = root, MaximumArtifactBytes = 1024 };
            var resolver = new ProviderDownloadArtifactResolver(store, options);
            var manifest = DownloadManifest();
            var sandbox = Sandbox(manifest, """
                registerExtension({ download: function() { return {
                  artifactId: 'claimed.flac', sha256: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                  sizeBytes: 10, verified: true, media: { mimeType: 'audio/flac', container: 'flac', codec: 'flac' }
                }; }});
                """);
            var adapter = new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options);
            var context = Context();
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(
                context.Actor.TenantId, context.Actor.EffectiveUserId, job, "fixture-extension", null, "claim"));

            var outcome = await adapter.DownloadAsync(context,
                new(Id(ProviderResourceKind.Track, "track-1"), job, workspace.Reference, ProviderAudioQuality.Any));

            Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
            Assert.Empty(store.Artifacts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ExtensionSdkManifest Manifest(ProviderCapabilityKind kind, params string[] hooks) => new(
        "fixture-extension", "Fixture", "1.0.0", "1", "index.js",
        [new ExtensionSdkCapability(kind, hooks, [ProviderAccountScope.User])], []);

    private static ExtensionSandbox Sandbox(ExtensionSdkManifest manifest, string script) => new(
        Path.GetTempPath(), """{"id":"fixture-extension","displayName":"Fixture","version":"1.0.0"}""", script,
        new HttpClientFactory(), NullLogger.Instance);

    private static ExtensionSdkManifest DownloadManifest() => new(
        "fixture-extension", "Fixture", "1.0.0", "1", "index.js",
        [new ExtensionSdkCapability(ProviderCapabilityKind.Download, ["download"],
            [ProviderAccountScope.User], AccountRequired: false)],
        [new ExtensionPermissionRequest(ExtensionPermissionKind.Network, "https://media.example.test/", true)]);

    private static DownloadFixtureState DownloadFixture(byte[] bytes, long maximumBytes, string artifactId)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-extension-download", Guid.NewGuid().ToString("N"));
        var store = new MemoryArtifactStore();
        var options = new ProviderDownloadWorkspaceOptions { RootPath = root, MaximumArtifactBytes = maximumBytes };
        var resolver = new ProviderDownloadArtifactResolver(store, options);
        var manifest = DownloadManifest();
        var permissions = new ExtensionRuntimePermissionSet(
            new HashSet<string>(["https://media.example.test/"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var sandbox = new ExtensionSandbox(root,
            """{"id":"fixture-extension","displayName":"Fixture","version":"1.0.0"}""",
            $$$"""
            registerExtension({ download: function() {
              const written = artifacts.download('https://media.example.test/audio', '{{{artifactId}}}');
              return { artifactId: written.artifactId, sha256: written.sha256, sizeBytes: written.sizeBytes,
                verified: written.verified, media: { mimeType: 'audio/flac', container: 'flac', codec: 'flac' } };
            }});
            """,
            new HttpClientFactory(new BytesHandler(bytes)), NullLogger.Instance, permissions,
            Path.Combine(root, "runtime"));
        var adapter = new ExtensionDownloadCapabilityAdapter(sandbox, manifest, artifacts: resolver, options: options);
        var context = Context();
        var job = Guid.CreateVersion7();
        var workspace = resolver.CreateWorkspaceAsync(new(
            context.Actor.TenantId, context.Actor.EffectiveUserId, job, "fixture-extension", null, "download"))
            .GetAwaiter().GetResult();
        return new(root, store, resolver, adapter, context, job, workspace,
            Id(ProviderResourceKind.Track, "track-1"));
    }

    private static ProviderExternalResourceId Id(ProviderResourceKind kind, string value) =>
        new("fixture-extension", kind, value);

    private static ProviderExecutionContext Context(string providerId = "fixture-extension")
    {
        var actor = new ProviderActorContext(Guid.CreateVersion7(), ProviderActorKind.User, Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "user"));
        return new ProviderExecutionContext(actor, providerId, null, null,
            new ProviderExecutionPolicy(new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, false, true, [providerId]),
            "extension-test", "extension-test-correlation", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None,
            "extension-test-idempotency");
    }

    private sealed class HttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler == null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class StatusHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);
        private readonly HttpStatusCode _fallback = statuses.LastOrDefault();
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var status = _statuses.TryDequeue(out var queued) ? queued : _fallback;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{}"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }

    private sealed class SignedSessionHandler : HttpMessageHandler
    {
        public HttpRequestMessage? SignedRequest { get; private set; }
        public string? ExchangeGrant { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            if (request.RequestUri!.AbsolutePath.EndsWith("/bootstrap", StringComparison.Ordinal))
                response = Json("{\"challenge_id\":\"challenge-1\"}");
            else if (request.RequestUri.AbsolutePath.EndsWith("/session/exchange", StringComparison.Ordinal))
            {
                var body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                ExchangeGrant = JsonDocument.Parse(body).RootElement.GetProperty("grant").GetString();
                response = Json("{\"session_id\":\"session-1\",\"session_secret\":\"secret-1\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");
            }
            else
            {
                SignedRequest = request;
                response = Json("{\"message\":\"catalog ok\"}");
            }
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    private sealed record DownloadFixtureState(string Root, MemoryArtifactStore Store,
        ProviderDownloadArtifactResolver Resolver, ExtensionDownloadCapabilityAdapter Adapter,
        ProviderExecutionContext Context, Guid JobId, ProviderDownloadWorkspace Workspace,
        ProviderExternalResourceId Track) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class MemoryArtifactStore : IProviderDownloadArtifactStore
    {
        public List<ProviderDownloadWorkspaceEntity> Workspaces { get; } = [];
        public List<ProviderDownloadArtifactEntity> Artifacts { get; } = [];
        public Task<ProviderDownloadWorkspaceEntity> CreateWorkspaceAsync(ProviderDownloadWorkspaceEntity value, CancellationToken token)
        {
            var existing = Workspaces.SingleOrDefault(item => item.WorkspaceId == value.WorkspaceId);
            if (existing != null) return Task.FromResult(existing);
            Workspaces.Add(value); return Task.FromResult(value);
        }
        public Task<ProviderDownloadWorkspaceEntity?> GetWorkspaceAsync(string id, CancellationToken token) =>
            Task.FromResult(Workspaces.SingleOrDefault(item => item.WorkspaceId == id));
        public Task<ProviderDownloadArtifactEntity> AddVerifiedAsync(ProviderDownloadArtifactEntity value, CancellationToken token)
        {
            var existing = Artifacts.SingleOrDefault(item => item.WorkspaceRecordId == value.WorkspaceRecordId &&
                                                              item.ProviderArtifactId == value.ProviderArtifactId);
            if (existing != null) return Task.FromResult(existing);
            Artifacts.Add(value); return Task.FromResult(value);
        }
        public Task<ProviderDownloadArtifactEntity?> FindByJobAsync(Guid tenantId, Guid jobId, string providerId, CancellationToken token) =>
            Task.FromResult(Artifacts.SingleOrDefault(item => item.TenantId == tenantId &&
                item.DurableJobId == jobId && item.ProviderId == providerId));
        public Task MarkPlacedAsync(Guid artifactId, Guid managedFileId, CancellationToken token) => Task.CompletedTask;
    }
}
