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
    public void StoredSpotiFlacDownloadManifest_GainsStreamingWithoutReinstallation()
    {
        const string stored = """
            {"id":"spotiflac-demo","displayName":"Demo","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
             "capabilities":[{"kind":"Download","hooks":["checkAvailability","download"],"accountScopes":["User"],"accountRequired":true}],
             "permissions":[],"compatibility":"spotiflac-v1"}
            """;

        var manifest = ExtensionSdkV1.ParseManifest(
            SpotiFlacExtensionCompatibility.EnsureDownloadStreamingCapability(stored));

        var streaming = Assert.Single(manifest.Capabilities,
            item => item.Kind == ProviderCapabilityKind.Streaming);
        Assert.True(streaming.AccountRequired);
        Assert.Contains(ProviderAccountScope.User, streaming.AccountScopes);
    }

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
                log.error('extension returned <redacted>');
                return {items:[]};
              }
            });
            """;
        var events = new List<(string Level, string Message)>();
        var logger = new CapturingLogger();
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
            logger,
            permissions);
        var recreatedSandbox = new ExtensionSandbox(
            Path.GetTempPath(),
            manifest,
            script,
            new HttpClientFactory(),
            logger,
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
        Assert.Equal(
            "Provider operation failed without a safe diagnostic.",
            logger.LastState!["Diagnostic"]);
        Assert.DoesNotContain("Message", logger.LastState.Keys);
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
                if (response.statusCode === 503) log.error('provider bad response 503');
                return {items:[], statusCode:response.statusCode, error:response.error || null};
              }
            });
            """;
        var handler = new StatusHandler(HttpStatusCode.Forbidden);
        var events = new List<(string Level, string Message)>();
        var permissions = new ExtensionRuntimePermissionSet(
            new HashSet<string>(["https://api.example.test/"]),
            new HashSet<string>(),
            new HashSet<string>(),
            LogSink: (level, message) => events.Add((level, message)));
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
        Assert.Empty(events);
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
            registerExtension({customSearch:function(){return [{id:'track-1',name:'Song',artists:'Artist',artist_id:'artist-1',album_id:'album-1',album_name:'Album',cover_url:'https://images.example.test/cover.jpg',item_type:'track'}];},getPlaylist:function(){return {tracks:[]};}});
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
        Assert.Contains("\"id\":\"artist-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"albumId\":\"album-1\"", json, StringComparison.Ordinal);
        Assert.Contains("https://images.example.test/cover.jpg", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotiFlacRuntimeAdapter_CanInspectLargeTextResponses()
    {
        const string prefix = "eyJ0eXAiOiJKV1QiLCJhbGciOiJFUzI1NiIsImtpZCI6IldlYlBsYXlLaWQifQ.";
        const string sourceManifest = """
            {"name":"large-response","displayName":"Large response","version":"1.0.0","description":"Fixture",
             "type":["metadata_provider"],"permissions":{"network":["music.apple.com"]}}
            """;
        const string script = """
            registerExtension({customSearch:function(){
              var response=http.get('https://music.apple.com/assets/index.js',{});
              var index=response.body.indexOf('eyJ0eXAiOiJKV1QiLCJhbGciOiJFUzI1NiIsImtpZCI6IldlYlBsYXlLaWQifQ.');
              return [{id:'track-1',name:String(index),artists:'Artist',item_type:'track'}];
            }});
            """;
        const int markerOffset = 3_900_000;
        const int responseBytes = 4 * 1024 * 1024 - 1;
        var body = new string('x', markerOffset) + prefix + new string('y', responseBytes - markerOffset - prefix.Length);
        var manifest = SpotiFlacExtensionCompatibility.NormalizeManifest(sourceManifest, script);
        var permissions = new ExtensionRuntimePermissionSet(
            new HashSet<string>(["https://music.apple.com/"], StringComparer.Ordinal),
            new HashSet<string>(), new HashSet<string>());
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), manifest, script,
            new HttpClientFactory(new BytesHandler(Encoding.UTF8.GetBytes(body))), NullLogger.Instance, permissions);

        var json = sandbox.InvokeJson("searchTracks", "{\"query\":\"Song\",\"page\":{\"limit\":1}}");

        Assert.Contains($"\"title\":\"{markerOffset}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotiFlacRuntimeAdapter_ForwardsHttpHeaders()
    {
        const string sourceManifest = """
            {"name":"headers","displayName":"Headers","version":"1.0.0","description":"Fixture",
             "type":["metadata_provider"],"permissions":{"network":["api.example.test"]}}
            """;
        const string script = """
            registerExtension({customSearch:function(){
              http.get('https://api.example.test/items',{Authorization:'Bearer expected',Origin:'https://example.test','Media-User-Token':'expected-user-token'});
              return [];
            }});
            """;
        var handler = new HeaderHandler();
        var manifest = SpotiFlacExtensionCompatibility.NormalizeManifest(sourceManifest, script);
        var permissions = new ExtensionRuntimePermissionSet(
            new HashSet<string>(["https://api.example.test/"], StringComparer.Ordinal),
            new HashSet<string>(), new HashSet<string>());
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), manifest, script,
            new HttpClientFactory(handler), NullLogger.Instance, permissions);

        sandbox.InvokeJson("searchTracks", "{\"query\":\"Song\",\"page\":{\"limit\":1}}");

        Assert.Equal("Bearer expected", handler.Authorization);
        Assert.Equal("https://example.test", handler.Origin);
        Assert.Equal("expected-user-token", handler.MediaUserToken);
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
    public async Task SpotiFlacDownloadProvider_PreparesAStreamAndDeletesItsTransientWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-spotiflac-stream", Guid.NewGuid().ToString("N"));
        try
        {
            const string sourceManifest = """
                {"name":"stream-demo","displayName":"Stream demo","version":"1.0.0","description":"Fixture",
                 "type":["download_provider"],"permissions":{"network":["media.example.test"]}}
                """;
            const string script = """
                registerExtension({checkAvailability:function(){return {available:true};},
                  download:function(id, quality, path){return file.download('https://media.example.test/audio', path, {});}});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(sourceManifest, script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            Assert.Contains(manifest.Capabilities, item => item.Kind == ProviderCapabilityKind.Streaming);
            var sandbox = new ExtensionSandbox(root, normalized, script,
                new HttpClientFactory(new BytesHandler([1, 2, 3, 4])), NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(["https://media.example.test/"]),
                    new HashSet<string>(), new HashSet<string>()), Path.Combine(root, "runtime"));
            var options = new ProviderDownloadWorkspaceOptions
            {
                RootPath = Path.Combine(root, "workspaces"),
                MaximumArtifactBytes = 1024
            };
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), options);
            var adapter = new ExtensionDownloadStreamingCapabilityAdapter(
                sandbox, manifest, null, resolver, options);
            var outcome = await adapter.GetStreamLeaseAsync(
                Context("spotiflac-stream-demo"),
                new ProviderStreamLeaseRequest(new(
                    "spotiflac-stream-demo", ProviderResourceKind.Track, "track-1")));

            Assert.True(outcome.IsSuccess, outcome.Error?.Kind.ToString());
            var lease = outcome.RequireValue();
            Assert.False(lease.SupportsByteRanges);
            Assert.Equal(1337, lease.Media.Bitrate);
            using (var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri))
            using (var response = await lease.ProtectedResponseFactory!(request, CancellationToken.None))
                Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
            Assert.Empty(Directory.EnumerateDirectories(options.RootPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Metadata_AllDeclaredAlbumAndArtistHooksMapTypedSchemas()
    {
        string[] hooks = ["searchTracks", "getTrack", "lookupByIsrc", "searchAlbums", "getAlbum", "searchArtists", "getArtist", "getArtistAlbums", "getArtistTracks"];
        var manifest = Manifest(ProviderCapabilityKind.Metadata, hooks);
        var sandbox = Sandbox(manifest, """
            const artist = { id: 'artist-1', name: 'Artist', artworkUrl: 'https://img.example/artist.jpg', snapshotVersion: 'a1' };
            const album = { id: 'album-1', title: 'Album', artists: [{ id: 'artist-1', name: 'Artist' }], trackCount: 8, snapshotVersion: 'b1' };
            const track = { id: 'track-1', title: 'Track', artists: [{ id: 'artist-1', name: 'Artist' }], albumId: 'album-1', albumTitle: 'Album', durationMs: 1234, bitrate: 320000, isrc: 'USABC1234567' };
            registerExtension({
              searchTracks: function() { return { items: [track] }; }, getTrack: function() { return track; }, lookupByIsrc: function() { return track; },
              searchAlbums: function() { return { items: [album], nextCursor: 'next' }; }, getAlbum: function() { return album; },
              searchArtists: function() { return { items: [artist] }; }, getArtist: function() { return artist; },
              getArtistAlbums: function() { return { items: [album] }; }, getArtistTracks: function() { return { items: [track] }; }
            });
            """);
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var search = new ProviderMetadataSearchRequest("fixture", new ProviderPageRequest(10));

        Assert.Equal("album-1", Assert.Single((await adapter.SearchAlbumsAsync(Context(), search)).RequireValue().Items).Id.Value);
        Assert.Equal("next", (await adapter.SearchAlbumsAsync(Context(), search)).RequireValue().NextCursor);
        Assert.Equal(8, (await adapter.GetAlbumAsync(Context(), new ProviderAlbumLookupRequest(Id(ProviderResourceKind.Album, "album-1")))).RequireValue().TrackCount);
        Assert.Equal("artist-1", Assert.Single((await adapter.SearchArtistsAsync(Context(), search)).RequireValue().Items).Id.Value);
        Assert.Equal("Artist", (await adapter.GetArtistAsync(Context(), new ProviderArtistLookupRequest(Id(ProviderResourceKind.Artist, "artist-1")))).RequireValue().Name);
        var artistItems = new ProviderArtistItemsRequest(Id(ProviderResourceKind.Artist, "artist-1"), new ProviderPageRequest(10));
        Assert.Equal("album-1", Assert.Single((await adapter.GetArtistAlbumsAsync(Context(), artistItems)).RequireValue().Items).Id.Value);
        Assert.Equal("track-1", Assert.Single((await adapter.GetArtistTracksAsync(Context(), artistItems)).RequireValue().Items).Id.Value);
        var track = (await adapter.LookupByIsrcAsync(Context(), new ProviderIsrcLookupRequest("USABC1234567"))).RequireValue();
        Assert.Equal("USABC1234567", track.Isrc);
        Assert.Equal(320_000, track.Bitrate);
    }

    [Fact]
    public void LegacyMetadataMapping_PreservesPositiveBitrate()
    {
        var sandbox = Sandbox(Manifest(ProviderCapabilityKind.Metadata, "getTrack"), """
            registerExtension({ getTrack: function() { return {
              id: 'track-1', name: 'Track', artists: ['Artist'], album: 'Album',
              duration_ms: 1234, bitrate: 320000
            }; }});
            """);

        Assert.Equal(320_000, sandbox.GetSong("track-1")!.Bitrate);
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

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://unapproved.example.test/audio")]
    public async Task Streaming_RejectsFilesystemAndUnapprovedNetworkSources(string sourceUri)
    {
        var manifest = Manifest(ProviderCapabilityKind.Streaming, "getStreamLease");
        var sandbox = Sandbox(manifest, """
            registerExtension({ getStreamLease: function(request) { return {
              leaseId: 'lease-1', sourceUri: 'SOURCE_URI', expiresAt: '2030-01-01T00:00:00Z',
              supportsByteRanges: true, supportsSeeking: true,
              media: { mimeType: 'audio/flac', container: 'flac', codec: 'flac' }, retryBehavior: 'RefreshLease'
            }; }});
            """.Replace("SOURCE_URI", sourceUri, StringComparison.Ordinal));
        var adapter = new ExtensionStreamingCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.GetStreamLeaseAsync(Context(includeAccount: true),
            new ProviderStreamLeaseRequest(Id(ProviderResourceKind.Track, "track-1")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
    }

    [Fact]
    public async Task Streaming_EnforcesAccountOriginCancellationAndTypedMediaFacts()
    {
        var manifest = Manifest(ProviderCapabilityKind.Streaming, "getStreamLease", "probeStream");
        var permissions = new ExtensionRuntimePermissionSet(
            new HashSet<string>(["https://media.example.test/"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        var sandbox = Sandbox(manifest, """
            registerExtension({
              getStreamLease: function(request) {
                if (request.trackId !== 'track-1' || request.requestedQuality !== 'Lossless' || request.rangeStart !== 4096)
                  throw new Error('request mismatch');
                return {
                  leaseId: 'lease-1', sourceUri: 'https://media.example.test/audio', expiresAt: '2030-01-01T00:00:00Z',
                  supportsByteRanges: true, supportsSeeking: true,
                  media: { mimeType: 'audio/flac', container: 'flac', codec: 'flac', bitrate: 1411000,
                    sampleRate: 44100, bitDepth: 16, channels: 2 }, retryBehavior: 'RefreshLease',
                  qualityDowngradeReason: 'The source catalog has no lossless copy.'
                };
              },
              probeStream: function(request) { return {
                available: request.requestedQuality === 'Lossless', observedAt: '2030-01-01T00:00:00Z',
                media: { mimeType: 'audio/flac', container: 'flac', codec: 'flac', sampleRate: 44100 }
              }; }
            });
            """, permissions);
        var adapter = new ExtensionStreamingCapabilityAdapter(sandbox, manifest);
        var request = new ProviderStreamLeaseRequest(
            Id(ProviderResourceKind.Track, "track-1"), ProviderAudioQuality.Lossless, 4096);

        var missingAccount = await adapter.GetStreamLeaseAsync(Context(), request);
        var leaseOutcome = await adapter.GetStreamLeaseAsync(Context(includeAccount: true), request);
        var probe = await adapter.ProbeStreamAsync(Context(includeAccount: true), request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = await adapter.GetStreamLeaseAsync(
            Context(includeAccount: true, cancellationToken: cancellation.Token), request);

        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, missingAccount.Error!.Kind);
        var lease = leaseOutcome.RequireValue();
        Assert.True(lease.SupportsByteRanges);
        Assert.True(lease.SupportsSeeking);
        Assert.Equal(1_411_000, lease.Media.Bitrate);
        Assert.Equal(44_100, lease.Media.SampleRate);
        Assert.Equal(16, lease.Media.BitDepth);
        Assert.Equal(2, lease.Media.Channels);
        Assert.Equal("The source catalog has no lossless copy.", lease.QualityDowngradeReason);
        Assert.True(probe.RequireValue().Available);
        Assert.Equal(44_100, probe.RequireValue().Media!.SampleRate);
        Assert.Equal(ProviderErrorKind.Canceled, canceled.Error!.Kind);
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
    public async Task PlaylistTrackMetadata_PreservesExtensionDuration()
    {
        var manifest = Manifest(ProviderCapabilityKind.Playlist, "getPlaylistTracks");
        var sandbox = Sandbox(manifest, """
            registerExtension({ getPlaylistTracks: function() { return {
              playlist: { id: 'playlist-1', name: 'Mix', owner: { providerUserId: 'owner' },
                sourceRevision: 'r1', trackCount: 1 },
              tracks: { items: [{ position: 0, trackId: 'track-1', metadata: {
                title: 'Track', artists: [{ name: 'Artist' }], albumTitle: 'Album', durationMs: 196456
              }}], isPartial: false }
            }; }});
            """);
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.GetPlaylistTracksAsync(
            Context(includeAccount: true),
            new ProviderPlaylistTracksRequest(
                Id(ProviderResourceKind.Playlist, "playlist-1"),
                new ProviderPageRequest()));

        Assert.True(outcome.IsSuccess, outcome.Error?.Kind.ToString());
        Assert.Equal(196_456, Assert.Single(outcome.Value!.Tracks.Items).Metadata!.Duration!.Value.TotalMilliseconds);
    }

    [Fact]
    public async Task PlaylistArtwork_UsesDeclaredHookApprovedOriginAndBoundedImage()
    {
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z0i8AAAAASUVORK5CYII=");
        var handler = new BytesHandler(bytes, "image/png");
        var manifest = new ExtensionSdkManifest(
            "fixture-extension", "Fixture", "1.0.0", "1", "index.js",
            [new ExtensionSdkCapability(ProviderCapabilityKind.Playlist,
                ["getUserPlaylists", "resolveArtwork"], [ProviderAccountScope.User])],
            [new ExtensionPermissionRequest(
                ExtensionPermissionKind.Network, "https://images.example.test/", true)]);
        const string script = """
            registerExtension({
              getUserPlaylists: function() { return { items: [{ id: 'playlist-1', name: 'Mix',
                owner: { providerUserId: 'owner' }, sourceRevision: 'r2', hasArtwork: true,
                artworkRevision: 'r2' }] }; },
              resolveArtwork: function(request) { return {
                artworkUrl: 'https://images.example.test/cover.png', revision: 'r2'
              }; }
            });
            """;
        var allowed = new ExtensionRuntimePermissionSet(
            new HashSet<string>(["https://images.example.test/"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        var sandbox = new ExtensionSandbox(
            Path.GetTempPath(),
            """{"id":"fixture-extension","displayName":"Fixture","version":"1.0.0"}""",
            script,
            new HttpClientFactory(handler),
            NullLogger.Instance,
            allowed);
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);
        var context = Context(includeAccount: true);

        var summary = Assert.Single((await adapter.GetUserPlaylistsAsync(
            context, new ProviderUserPlaylistsRequest(new ProviderPageRequest()))).RequireValue().Items);
        var outcome = await adapter.ResolveArtworkAsync(
            context,
            new ProviderPlaylistArtworkRequest(summary.Artwork!, 1024));

        Assert.Equal("playlist-1", summary.Artwork!.ResourceId!.Value);
        Assert.Equal("r2", summary.Artwork.Revision);
        Assert.True(outcome.IsSuccess, outcome.Error?.Kind.ToString());
        Assert.Equal(bytes, outcome.Value!.Bytes);
        Assert.Equal("image/png", outcome.Value.ContentType);
        Assert.Equal(1, handler.CallCount);
        Assert.True(ExtensionPlaylistCapabilityAdapter.IsAllowedArtworkDimensions(4_000, 4_000));
        Assert.False(ExtensionPlaylistCapabilityAdapter.IsAllowedArtworkDimensions(4_001, 4_000));

        var deniedSandbox = new ExtensionSandbox(
            Path.GetTempPath(),
            """{"id":"fixture-extension","displayName":"Fixture","version":"1.0.0"}""",
            script,
            new HttpClientFactory(handler),
            NullLogger.Instance,
            ExtensionRuntimePermissionSet.None);
        var denied = await new ExtensionPlaylistCapabilityAdapter(deniedSandbox, manifest)
            .ResolveArtworkAsync(context, new ProviderPlaylistArtworkRequest(summary.Artwork!, 1024));
        Assert.Equal(ProviderErrorKind.Forbidden, denied.Error!.Kind);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Intelligence_MapsAnalysisDiscoveryAndOptionalSonicContracts()
    {
        string[] hooks = ["startAnalysis", "getAnalysisProgress", "getClusters", "recommend",
            "search", "findPath", "blend", "getMap", "disconnect"];
        var manifest = new ExtensionSdkManifest("fixture-extension", "Fixture", "1.0.0", "1", "index.js",
        [
            new ExtensionSdkCapability(ProviderCapabilityKind.Intelligence, hooks, [ProviderAccountScope.User]),
            new ExtensionSdkCapability(ProviderCapabilityKind.Health, ["probeIntelligence"], [ProviderAccountScope.User])
        ], []);
        var sandbox = Sandbox(manifest, """
            const track = { trackId: 'track-1', title: 'Track', artist: 'Artist', album: 'Album',
              score: 0.9, clusterId: 'cluster-1', explanation: 'Similar sound' };
            registerExtension({
              startAnalysis: function() { return { jobId: 'job-1', state: 'Queued', completed: 0, total: 10 }; },
              getAnalysisProgress: function() { return { jobId: 'job-1', state: 'Running', completed: 4, total: 10 }; },
              getClusters: function() { return { items: [{ id: 'cluster-1', name: 'Cluster', tracks: [track] }] }; },
              recommend: function() { return { items: [track] }; },
              search: function() { return { items: [track] }; },
              findPath: function() { return { items: [track, { trackId: 'track-2', title: 'Bridge', artist: 'Artist', score: 0.8 }], totalDistance: 0.4 }; },
              blend: function() { return { items: [track] }; },
              getMap: function() { return { items: [{ trackId: 'track-1', title: 'Track', artist: 'Artist', x: 0.25, y: -0.5 }], projection: 'umap', nextCursor: 'next', snapshotVersion: 'map-1' }; },
              disconnect: function() { return { disconnected: true }; },
              probeIntelligence: function() { return { status: 'Healthy', observedAt: '2030-01-01T00:00:00Z', latencyMs: 1 }; }
            });
            """);
        var intelligence = new ExtensionIntelligenceCapabilityAdapter(sandbox, manifest);
        var health = new ExtensionHealthCapabilityAdapter(sandbox, manifest);
        var context = Context(includeAccount: true);

        Assert.Equal("job-1", (await intelligence.StartAnalysisAsync(context)).RequireValue().JobId);
        Assert.Equal(4, (await intelligence.GetAnalysisProgressAsync(context, "job-1")).RequireValue().Completed);
        Assert.Equal("cluster-1", Assert.Single((await intelligence.GetClustersAsync(context)).RequireValue()).Id);
        Assert.Equal("Similar sound", Assert.Single((await intelligence.RecommendAsync(context, ["seed"], 10)).RequireValue()).Explanation);
        Assert.Equal("Track", Assert.Single((await intelligence.SearchAsync(context, "lyrics", true, 10)).RequireValue()).Title);
        Assert.Equal(["track-1", "track-2"], (await intelligence.FindPathAsync(context, "track-1", "track-2", 10)).RequireValue().Tracks.Select(item => item.TrackId));
        Assert.Equal("track-1", Assert.Single((await intelligence.BlendAsync(context, ["track-1"], ["track-2"], 10)).RequireValue()).TrackId);
        var map = (await intelligence.GetMapAsync(context, new ProviderPageRequest(10))).RequireValue();
        Assert.Equal("umap", map.Projection);
        Assert.Equal(0.25, Assert.Single(map.Items).X);
        Assert.Equal("next", map.NextCursor);
        Assert.True((await intelligence.DisconnectAsync(context)).RequireValue());
        Assert.Equal(ProviderProbeStatus.Healthy,
            (await health.ProbeAsync(context, new(ProviderCapabilityKind.Intelligence))).RequireValue().Status);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            intelligence.RecommendAsync(context, ["seed"], 0));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            intelligence.BlendAsync(context, ["same"], ["same"], 10));
    }

    [Fact]
    public async Task Intelligence_RejectsMalformedAndOversizedExtensionResults()
    {
        string[] hooks = ["startAnalysis", "getAnalysisProgress", "getClusters", "recommend",
            "search", "findPath", "blend", "getMap"];
        var manifest = new ExtensionSdkManifest("fixture-extension", "Fixture", "1.0.0", "1", "index.js",
        [
            new ExtensionSdkCapability(ProviderCapabilityKind.Intelligence, hooks, [ProviderAccountScope.User])
        ], []);
        var sandbox = Sandbox(manifest, """
            function tracks(count) {
              var result = [];
              for (var i = 0; i < count; i++) result.push({ trackId: 'track-' + i, title: 'Track', artist: 'Artist', score: 0.9 });
              return result;
            }
            registerExtension({
              startAnalysis: function() { return { jobId: '', state: 'Queued', completed: 0, total: 1 }; },
              getAnalysisProgress: function() { return { jobId: 'job-1', state: 'Running', completed: 2, total: 1 }; },
              getClusters: function() { return { items: [
                { id: 'one', name: 'One', tracks: [] }, { id: 'two', name: 'Two', tracks: [] }
              ] }; },
              recommend: function() { return { items: tracks(2) }; },
              search: function() { return { items: [{ trackId: 'track-1', title: 'Track', artist: 'Artist', score: 0.9, album: Array(502).join('x') }] }; },
              findPath: function() { return { items: tracks(3), totalDistance: 0.4 }; },
              blend: function() { return { items: tracks(2) }; },
              getMap: function() { return { items: [{ trackId: 'track-1', title: 'Track', artist: 'Artist', x: 2, y: 0 }], projection: 'umap' }; }
            });
            """);
        var intelligence = new ExtensionIntelligenceCapabilityAdapter(sandbox, manifest);
        var context = Context(includeAccount: true);

        static void Transient<T>(ProviderOutcome<T> outcome) =>
            Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);

        Transient(await intelligence.StartAnalysisAsync(context));
        Transient(await intelligence.GetAnalysisProgressAsync(context, "job-1"));
        Transient(await intelligence.GetClustersAsync(context, 1));
        Transient(await intelligence.RecommendAsync(context, ["seed"], 1));
        Transient(await intelligence.SearchAsync(context, "query", false, 1));
        Transient(await intelligence.FindPathAsync(context, "start", "end", 2));
        Transient(await intelligence.BlendAsync(context, ["start"], [], 1));
        Transient(await intelligence.GetMapAsync(context, new ProviderPageRequest(1)));
    }

    [Fact]
    public async Task Intelligence_OmittedSonicHooksReportNotSupported()
    {
        var manifest = Manifest(ProviderCapabilityKind.Intelligence, "recommend");
        var adapter = new ExtensionIntelligenceCapabilityAdapter(Sandbox(manifest,
            "registerExtension({ recommend: function() { return { items: [] }; } });"), manifest);
        var context = Context();

        Assert.Equal(ProviderErrorKind.NotSupported,
            (await adapter.FindPathAsync(context, "one", "two", 2)).Error!.Kind);
        Assert.Equal(ProviderErrorKind.NotSupported,
            (await adapter.BlendAsync(context, ["one"], [], 1)).Error!.Kind);
        Assert.Equal(ProviderErrorKind.NotSupported,
            (await adapter.GetMapAsync(context, new ProviderPageRequest(1))).Error!.Kind);
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

    private static ExtensionSandbox Sandbox(
        ExtensionSdkManifest manifest,
        string script,
        ExtensionRuntimePermissionSet? permissions = null) => new(
        Path.GetTempPath(), """{"id":"fixture-extension","displayName":"Fixture","version":"1.0.0"}""", script,
        new HttpClientFactory(), NullLogger.Instance, permissions);

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

    private static ProviderExecutionContext Context(
        string providerId = "fixture-extension",
        bool includeAccount = false,
        CancellationToken cancellationToken = default)
    {
        var actor = new ProviderActorContext(Guid.CreateVersion7(), ProviderActorKind.User, Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "user"));
        var account = includeAccount
            ? new ProviderAccountContext(Guid.CreateVersion7(), providerId, ProviderAccountScope.User, 1,
                tenantId: actor.TenantId, ownerUserId: actor.EffectiveUserId)
            : null;
        return new ProviderExecutionContext(actor, providerId, account, null,
            new ProviderExecutionPolicy(new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, false, true, [providerId]),
            "extension-test", "extension-test-correlation", DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken,
            "extension-test-idempotency");
    }

    private sealed class HttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler == null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
    }

    private sealed class BytesHandler(byte[] bytes, string? contentType = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (contentType != null)
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
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

    private sealed class HeaderHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? Origin { get; private set; }
        public string? MediaUserToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.TryGetValues("Authorization", out var authorization)
                ? authorization.Single() : null;
            Origin = request.Headers.TryGetValues("Origin", out var origin) ? origin.Single() : null;
            MediaUserToken = request.Headers.TryGetValues("Media-User-Token", out var mediaUserToken)
                ? mediaUserToken.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
                RequestMessage = request
            });
        }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IReadOnlyDictionary<string, object?>? LastState { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                LastState = fields.ToDictionary(item => item.Key, item => item.Value);
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
