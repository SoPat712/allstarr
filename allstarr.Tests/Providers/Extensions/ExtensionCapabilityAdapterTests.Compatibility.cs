using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Fact]
    public void FetchJson_ReturnsUndefinedForMalformedResponseText()
    {
        using var result = ProbeHost("""
            var response = fetch('https://api.example.test/metadata');
            try { return response.json() === undefined ? 'undefined' : 'parsed'; }
            catch (_) { return 'threw'; }
            """, new FetchJsonContractHandler());

        Assert.Equal("undefined", result.RootElement.GetString());
    }

    [Fact]
    public void HttpCookies_PreserveSeparateResponseValuesAndStayWithinOneRuntime()
    {
        var handler = new CookieContractHandler();
        using (var result = ProbeHost("""
            var response = http.get('https://api.example.test/start');
            http.get('https://api.example.test/private/next');
            http.get('https://api.example.test/other');
            http.get('https://api.example.test/expire');
            http.get('https://api.example.test/private/next');
            if (!Array.isArray(response.headers['set-cookie'])) throw new Error('header values must be an array');
            return response.headers['set-cookie'];
            """, handler))
        {
            Assert.Equal(2, result.RootElement.GetArrayLength());
            Assert.StartsWith("first=one;", result.RootElement[0].GetString());
            Assert.StartsWith("second=two;", result.RootElement[1].GetString());
        }
        Assert.Contains("first=one", handler.Cookies[1], StringComparison.Ordinal);
        Assert.Contains("second=two", handler.Cookies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("second=two", handler.Cookies[2], StringComparison.Ordinal);
        Assert.DoesNotContain("first=one", handler.Cookies[4], StringComparison.Ordinal);
        using var isolated = ProbeHost("http.get('https://api.example.test/private/next'); return true;", handler);
        Assert.Equal("", handler.Cookies[5]);
    }

    [Fact]
    public void HttpCookies_ApplyRedirectCookiesAndAllowClearing()
    {
        var handler = new CookieContractHandler();
        using var result = ProbeHost("""
            http.get('https://api.example.test/redirect');
            var cleared = http.clearCookies();
            http.get('https://api.example.test/private/next');
            return cleared;
            """, handler);

        Assert.True(result.RootElement.GetBoolean());
        Assert.Equal("redirect=three", handler.Cookies[1]);
        Assert.Equal("", handler.Cookies[2]);
    }

    [Theory]
    [InlineData("first=one; Path=/; Secure", "first=one")]
    [InlineData("bad=value; Domain=other.test; Path=/", "")]
    public void HttpCookies_DoNotSendCookiesOutsideTheirDomain(string cookie, string expected)
    {
        var handler = new CookieContractHandler(cookie);
        using var result = ProbeHost("""
            http.get('https://api.example.test/custom');
            http.get('https://api.example.test/private/next');
            return true;
            """, handler);

        Assert.Equal(expected, handler.Cookies[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpCookies_ShareTheExtensionJarWithFileAndSegmentDownloads(bool redirect)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-cookie-download", Guid.NewGuid().ToString("N"));
        try
        {
            const string source = """
                {"name":"fixture","version":"1.0.0","type":["download_provider"],"permissions":{"network":["api.example.test"]}}
                """;
            var script = """
                registerExtension({download:function(id,quality,path) {
                  http.get('https://api.example.test/start');
                  var first = file.download('https://api.example.test/private/next', 'first.audio');
                  if (!first.success) throw new Error('file download failed');
                  return file.downloadSegments(['https://api.example.test/private/next'], path);
                }});
                """;
            if (redirect) script = script.Replace("/private/next", "/redirect", StringComparison.Ordinal);
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var handler = new CookieContractHandler();
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(handler), NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(["https://api.example.test/"]), new HashSet<string>(), new HashSet<string>()), root);
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(),
                new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 4096 }, audioVerifier: FixtureAudioVerifier.Instance);
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(Guid.CreateVersion7(), job, manifest.Id, null, "download"));
            using var scope = ExtensionArtifactInvocationScope.Open(resolver, workspace.Reference, job, manifest.Id, 4096, CancellationToken.None);

            Assert.NotNull(sandbox.InvokeJson("download", """{"trackId":"track-1"}"""));

            Assert.Equal(redirect ? 5 : 3, handler.Cookies.Count);
            for (var index = 1; index < handler.Cookies.Count; index++)
            {
                var cookies = handler.Cookies[index];
                Assert.Contains("first=one", cookies, StringComparison.Ordinal);
                if (handler.Paths[index].StartsWith("/private", StringComparison.Ordinal))
                    Assert.Contains("second=two", cookies, StringComparison.Ordinal);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackEnrichment_UsesSourceShapeAndRetainsMetadataInTheTypedAdapter(bool fromSearch)
    {
        const string script = """
            var calls = 0;
            function track() { return {id:'track-1',name:'Song',artists:'Artist',album_id:'album-1',album_name:'Album',privateField:'kept'}; }
            registerExtension({
              searchTracks:function(){return {tracks:[track()]};},
              getTrack:function(){return track();},
              getArtist:function(){return {id:String(calls),name:'Counter'};},
              enrichTrack:function(value) {
                if (value.name !== 'Song' || value.artists !== 'Artist' || value.privateField !== 'kept') throw new Error('wrong source shape');
                calls++;
                if (calls !== 1) throw new Error('duplicate enrichment');
                return Object.assign({}, value, {isrc:'USFIX2600001',duration_ms:123000,track_number:2,disc_number:1,
                  total_tracks:9,total_discs:2,release_date:'2026-01-02',genre:'Rock',genres:['Rock','Pop'],label:'Fixture',album_artist:'Artist',bitrate:320,
                  album_artists:[{id:'album-artist',name:'Album Artist'}],composer:'Composer',contributors:['Contributor'],
                  upc:'0123456789012',musical_key:'D minor',description:'Recording notes',explicit:false,
                  spotify_id:'spotify-track',tidal_id:'tidal-track',qobuz_id:'qobuz-track',deezer_id:'deezer-track',
                  container:'flac',codec:'flac',sample_rate:96000,bit_depth:24,channels:2,size:9876543,
                  replay_gain:{track_gain:0,album_gain:-6.5,track_peak:0.98,album_peak:1},
                  images:[{url:'https://cdn.example.test/small.jpg',width:64,height:64},{url:'https://cdn.example.test/large.jpg',width:1400,height:1400}]});
              }
            });
            """;
        var (sandbox, manifest) = CompatibilitySandbox(script);
        if (fromSearch) sandbox.InvokeJson("searchTracks", """{"query":"Song"}""");
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var request = new ProviderTrackLookupRequest(new(manifest.Id, ProviderResourceKind.Track, "track-1"));
        var outcome = await adapter.GetTrackAsync(Context(manifest.Id), request);
        var track = outcome.RequireValue();

        Assert.Equal("USFIX2600001", track.Isrc);
        Assert.Equal(TimeSpan.FromSeconds(123), track.Duration);
        Assert.Equal(2, track.TrackNumber);
        Assert.Equal(1, track.DiscNumber);
        Assert.Equal(9, track.TotalTracks);
        Assert.Equal(2, track.TotalDiscs);
        Assert.Equal("2026-01-02", track.ReleaseDate);
        Assert.Equal("Rock", track.Genre);
        Assert.Equal("Fixture", track.Label);
        Assert.Equal("Artist", track.AlbumArtist);
        Assert.Equal(320, track.Bitrate);
        Assert.Equal(["Rock", "Pop"], track.Genres);
        Assert.Equal("0123456789012", track.Upc);
        Assert.Equal("D minor", track.MusicalKey);
        Assert.Equal("Recording notes", track.Notes);
        Assert.False(track.IsExplicit);
        Assert.Equal("album-artist", Assert.Single(track.AlbumArtists).ArtistId!.Value);
        Assert.Equal("Composer", track.Composer);
        Assert.Equal(["Contributor"], track.Contributors);
        Assert.Equal("spotify-track", track.ProviderIds["spotify"]);
        Assert.Equal("tidal-track", track.ProviderIds["tidal"]);
        Assert.Equal("qobuz-track", track.ProviderIds["qobuz"]);
        Assert.Equal("deezer-track", track.ProviderIds["deezer"]);
        Assert.Equal("flac", track.Media!.Container);
        Assert.Equal(96000, track.Media.SampleRate);
        Assert.Equal(24, track.Media.BitDepth);
        Assert.Equal(2, track.Media.Channels);
        Assert.Equal(9876543, track.Size);
        Assert.Equal(0, track.ReplayGain!.TrackGain);
        Assert.Equal(-6.5, track.ReplayGain.AlbumGain);
        Assert.Equal(0.98, track.ReplayGain.TrackPeak);
        Assert.Equal("https://cdn.example.test/large.jpg", track.Artwork!.PublicUri!.AbsoluteUri);
        sandbox.InvokeJson("searchTracks", """{"query":"Song"}""");
        Assert.Equal("USFIX2600001", (await adapter.GetTrackAsync(Context(manifest.Id), request)).RequireValue().Isrc);
        using var counter = JsonDocument.Parse(sandbox.InvokeJson("getArtist", """{"id":"counter"}""")!);
        Assert.Equal("1", counter.RootElement.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("isExplicit:true,upc:'0123456789012',edition:'Deluxe'", true, "0123456789012", "Deluxe")]
    [InlineData("isExplicit:false,upc:'0123456789012',edition:'Deluxe'", false, "0123456789012", "Deluxe")]
    [InlineData("is_explicit:false,external_ids:{upc:'0123456789012'},album_version:'Deluxe'", false, "0123456789012", "Deluxe")]
    [InlineData("explicit_lyrics:false", false, null, null)]
    [InlineData("explicit:true", true, null, null)]
    [InlineData("contentRating:'explicit'", true, null, null)]
    [InlineData("content_rating:'clean'", false, null, null)]
    [InlineData("content_rating:'cleaned'", false, null, null)]
    [InlineData("isExplicit:'false',explicit_lyrics:0,version:'resource-v2'", null, null, null)]
    [InlineData("", null, null, null)]
    public async Task MetadataCompatibility_AlbumEvidencePreservesNullableSourceFacts(
        string evidence, bool? expectedExplicit, string? expectedUpc, string? expectedEdition)
    {
        var script = $$$"""
            registerExtension({getAlbum:function(){return {
              id:'album-1',name:'Album',artists:'Artist',{{{evidence}}}{{{(evidence.Length > 0 ? "," : "")}}}
              tracks:[{id:'track-1',name:'Song',artists:'Artist',explicit:true}]};}});
            """;
        var (sandbox, manifest) = CompatibilitySandbox(script);
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);

        var album = (await adapter.GetAlbumAsync(Context(manifest.Id),
            new(new(manifest.Id, ProviderResourceKind.Album, "album-1")))).RequireValue();

        Assert.Equal(expectedExplicit, album.IsExplicit);
        Assert.Equal(expectedUpc, album.Upc);
        Assert.Equal(expectedEdition, album.Edition);
        Assert.True(Assert.Single(album.Tracks).IsExplicit);
    }

    [Theory]
    [InlineData("apple-music", "apple-musickit", "123")]
    [InlineData("deezer", "deezer", "deezer:123")]
    [InlineData("spotify-web", "spotify", "opaqueCaseID")]
    public async Task MetadataCompatibility_AlbumCatalogAliasUsesDeclaredSource(string source, string provider, string id)
    {
        var (sandbox, manifest) = CompatibilitySandbox($$$"""
            registerExtension({getAlbum:function(id){return {
              id:id,name:'Album',artists:'Artist',provider_id:'{{{source}}}'
            };}});
            """);
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var album = (await adapter.GetAlbumAsync(Context(manifest.Id),
            new(new(manifest.Id, ProviderResourceKind.Album, id)))).RequireValue();
        Assert.Equal(id.StartsWith(provider + ":", StringComparison.Ordinal) ? id[(provider.Length + 1)..] : id,
            album.ProviderIds[provider]);
    }

    [Fact]
    public async Task MetadataCompatibility_PreservesCamelCaseFactsAndDescriptions()
    {
        const string script = """
            registerExtension({
              getTrack:function(){return {id:'track-1',title:'Song',artists:[{id:'artist-1',name:'Artist'}],
                albumId:'album-1',albumTitle:'Album',durationMs:4567,isExplicit:false,trackNumber:2,discNumber:1,
                totalTracks:8,totalDiscs:2,releaseDate:'2026-02-03',albumArtists:[{id:'artist-2',name:'Album Artist'}],
                spotifyId:'spotify-track',musicalKey:'F',notes:'Notes',providerIds:{musicbrainz:'recording-1'},
                sampleRate:48000,bitDepth:24,channels:2,size:2345,replayGain:{trackGain:0,albumPeak:-1}};},
              getAlbum:function(){return {id:'album-1',title:'Album',artists:[{id:'artist-1',name:'Artist'}],
                trackCount:8,releaseDate:'2026-02-03',genre:'Pop',label:'Label',description:'Album notes',
                providerIds:{spotify:'spotify-album'},tracks:[{id:'track-1',name:'Song',artists:'Artist'}]};},
              getArtist:function(){return {id:'artist-1',name:'Artist',biography:'Artist biography',genres:['Hip Hop','Soul'],
                providerIds:{musicbrainz:'artist-mbid'},lastFmUrl:'https://www.last.fm/music/Artist'};}
            });
            """;
        var (sandbox, manifest) = CompatibilitySandbox(script);
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);

        var track = (await adapter.GetTrackAsync(Context(manifest.Id), new(new(manifest.Id, ProviderResourceKind.Track, "track-1")))).RequireValue();
        var album = (await adapter.GetAlbumAsync(Context(manifest.Id), new(new(manifest.Id, ProviderResourceKind.Album, "album-1")))).RequireValue();
        var artist = (await adapter.GetArtistAsync(Context(manifest.Id), new(new(manifest.Id, ProviderResourceKind.Artist, "artist-1")))).RequireValue();

        Assert.Equal(TimeSpan.FromMilliseconds(4567), track.Duration);
        Assert.False(track.IsExplicit);
        Assert.Equal(2, track.TrackNumber);
        Assert.Equal(1, track.DiscNumber);
        Assert.Equal(8, track.TotalTracks);
        Assert.Equal(2, track.TotalDiscs);
        Assert.Equal("Album", track.AlbumTitle);
        Assert.Equal("2026-02-03", track.ReleaseDate);
        Assert.Equal("spotify-track", track.SpotifyId);
        Assert.Equal("recording-1", track.ProviderIds["musicbrainz"]);
        Assert.Equal("F", track.MusicalKey);
        Assert.Equal("Notes", track.Notes);
        Assert.Equal("artist-2", Assert.Single(track.AlbumArtists).ArtistId!.Value);
        Assert.Equal(48000, track.Media!.SampleRate);
        Assert.Equal("unknown", track.Media.Codec);
        Assert.Equal(0, track.ReplayGain!.TrackGain);
        Assert.Null(track.ReplayGain.AlbumPeak);
        Assert.Equal(8, album.TrackCount);
        Assert.Equal("Pop", album.Genre);
        Assert.Equal("Label", album.Label);
        Assert.Equal("Album notes", album.Notes);
        Assert.Equal("spotify-album", album.ProviderIds["spotify"]);
        Assert.Single(album.Tracks);
        Assert.Equal("Artist biography", artist.Biography);
        Assert.Equal(["Hip Hop", "Soul"], artist.Genres);
        Assert.Equal("artist-mbid", artist.ProviderIds["musicbrainz"]);
        Assert.Equal("https://www.last.fm/music/Artist", artist.LastFmUrl);
    }

    [Theory]
    [InlineData("account", false)]
    [InlineData("account", true)]
    [InlineData("revision", false)]
    [InlineData("revision", true)]
    [InlineData("anonymous", false)]
    [InlineData("anonymous", true)]
    [InlineData("selected", false)]
    [InlineData("selected", true)]
    public async Task TrackEnrichment_InvalidatesCachedTracksWhenAccountContextChanges(string change, bool enrichFirst)
    {
        const string script = """
            var reads = 0, enrichments = 0;
            function track() { reads++; return {id:'track-1',name:'Song '+reads,artists:'Artist'}; }
            registerExtension({
              searchTracks:function(){return {tracks:[track()]};},
              getTrack:function(){return track();},
              enrichTrack:function(value){enrichments++;return Object.assign({},value,{label:'Pass '+enrichments});}
            });
            """;
        var (sandbox, manifest) = CompatibilitySandbox(script);
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var original = Context(manifest.Id);
        var account = new ProviderAccountContext(Guid.CreateVersion7(), manifest.Id, ProviderAccountScope.Personal, 1,
            ownerUserId: original.Actor.EffectiveUserId);
        ProviderExecutionContext WithAccount(ProviderAccountContext? selected) => new(original.Actor, manifest.Id, selected,
            original.Policy, "metadata", "fixture", original.Deadline, default);
        var first = WithAccount(change == "selected" ? null : account);
        var next = WithAccount(change switch
        {
            "account" => new ProviderAccountContext(Guid.CreateVersion7(), manifest.Id, ProviderAccountScope.Personal, 1,
                ownerUserId: original.Actor.EffectiveUserId),
            "revision" => new ProviderAccountContext(account.AccountId, manifest.Id, ProviderAccountScope.Personal, 2,
                ownerUserId: original.Actor.EffectiveUserId),
            "anonymous" => null,
            _ => account
        });
        var request = new ProviderTrackLookupRequest(new(manifest.Id, ProviderResourceKind.Track, "track-1"));
        Assert.Equal("Song 1", Assert.Single((await adapter.SearchTracksAsync(first, new("Song", new()))).RequireValue().Items).Title);
        if (enrichFirst)
        {
            Assert.Equal("Pass 1", (await adapter.GetTrackAsync(first, request)).RequireValue().Label);
            Assert.Equal("Pass 1", (await adapter.GetTrackAsync(first, request)).RequireValue().Label);
        }

        var detail = (await adapter.GetTrackAsync(next, request)).RequireValue();

        Assert.Equal("Song 2", detail.Title);
        Assert.Equal(enrichFirst ? "Pass 2" : "Pass 1", detail.Label);
        var repeated = Assert.Single((await adapter.SearchTracksAsync(next, new("Song", new()))).RequireValue().Items);
        Assert.Equal(detail.Title, repeated.Title);
        Assert.Equal(detail.Label, repeated.Label);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TrackEnrichment_InvalidatesCachedTracksWhenSettingsChange(bool initialize, bool enrichFirst)
    {
        const string source = """
            {"name":"fixture","version":"1.0.0","type":["metadata_provider"],
             "settings":[{"key":"region","type":"string","default":"first"}]}
            """;
        var initializeHook = initialize ? "initialize:function(settings){region=settings.region;}," : "";
        var regionValue = initialize ? "region" : "host.SettingGet('region')";
        var script = $$"""
            var region = '', reads = 0;
            function selectedRegion() { return {{regionValue}}; }
            function track() { reads++; return {id:'track-1',name:selectedRegion()+' '+reads,artists:'Artist'}; }
            registerExtension({
              {{initializeHook}}
              searchTracks:function(){return {tracks:[track()]};},
              getTrack:function(){return track();},
              enrichTrack:function(value){return Object.assign({},value,{label:selectedRegion()});}
            });
            """;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), normalized, script, new HttpClientFactory(), NullLogger.Instance,
            ExtensionRuntimePermissionSet.None with { SettingKeys = new HashSet<string>(["region"]) });
        var settings = new CompatibilitySettingsAccessor();
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest, settings);
        var context = Context(manifest.Id, includeAccount: true);
        var request = new ProviderTrackLookupRequest(new(manifest.Id, ProviderResourceKind.Track, "track-1"));
        Assert.Equal("first 1", Assert.Single((await adapter.SearchTracksAsync(context, new("Song", new()))).RequireValue().Items).Title);
        if (enrichFirst)
            Assert.Equal("first", (await adapter.GetTrackAsync(context, request)).RequireValue().Label);
        settings.Region = "second";

        var detail = (await adapter.GetTrackAsync(context, request)).RequireValue();

        Assert.Equal("second 2", detail.Title);
        Assert.Equal("second", detail.Label);
        var repeated = Assert.Single((await adapter.SearchTracksAsync(context, new("Song", new()))).RequireValue().Items);
        Assert.Equal(detail.Title, repeated.Title);
        Assert.Equal(detail.Label, repeated.Label);
    }

    [Theory]
    [InlineData("throw new Error('unavailable');")]
    [InlineData("return null;")]
    [InlineData("return {id:'other-track',name:'Wrong',artists:'Other'};")]
    public void TrackEnrichment_KeepsOriginalMetadataOnFailureOrIdentityChange(string body)
    {
        var script = "registerExtension({getTrack:function(){return {id:'track-1',name:'Song',artists:'Artist'};},enrichTrack:function(track){" + body + "}});";
        var (sandbox, _) = CompatibilitySandbox(script);
        using var result = JsonDocument.Parse(sandbox.InvokeJson("getTrack", """{"id":"track-1"}""")!);

        Assert.Equal("track-1", result.RootElement.GetProperty("id").GetString());
        Assert.Equal("Song", result.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("{strategy:'ffmpeg.mov_key',key:'fixture'}")]
    [InlineData("{strategy:'unknown'}")]
    public void DownloadPostprocessing_RejectsUnprocessedEncryptionBeforeCommitting(string descriptor)
    {
        var script = "registerExtension({download:function(){return {success:true,file_path:'encrypted.m4a',decryption:" + descriptor + "};}});";
        var (sandbox, _) = CompatibilitySandbox(script, "download_provider");

        var error = Assert.ThrowsAny<Exception>(() => sandbox.InvokeJson("download", """{"trackId":"track-1"}"""));
        Assert.Contains("decryption failed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture", error.Message, StringComparison.Ordinal);
    }

    private static (ExtensionSandbox Sandbox, ExtensionSdkManifest Manifest) CompatibilitySandbox(string script, string type = "metadata_provider")
    {
        var source = "{\"name\":\"fixture\",\"version\":\"1.0.0\",\"type\":[\"" + type + "\"]}";
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        return (new ExtensionSandbox(Path.GetTempPath(), normalized, script, new HttpClientFactory(),
            NullLogger.Instance, ExtensionRuntimePermissionSet.None), manifest);
    }

    private sealed class CookieContractHandler(string? customCookie = null) : HttpMessageHandler
    {
        public List<string> Cookies { get; } = [];
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Cookies.Add(request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "");
            Paths.Add(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{}") };
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/start":
                    response.Headers.Add("Set-Cookie", new[] { "first=one; Path=/; Secure; HttpOnly", "second=two; Path=/private; Secure" });
                    break;
                case "/expire":
                    response.Headers.Add("Set-Cookie", "first=; Max-Age=0; Path=/");
                    break;
                case "/redirect":
                    response.StatusCode = HttpStatusCode.Found;
                    response.Headers.Location = new Uri("/private/next", UriKind.Relative);
                    response.Headers.Add("Set-Cookie", "redirect=three; Path=/; Secure");
                    break;
                case "/custom":
                    response.Headers.Add("Set-Cookie", customCookie!);
                    break;
            }
            return Task.FromResult(response);
        }
    }

    private sealed class FetchJsonContractHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("metadata unavailable") });
    }

    private sealed class CompatibilitySettingsAccessor : allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor
    {
        public string Region { get; set; } = "first";

        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken) => operation(JsonSerializer.SerializeToUtf8Bytes(new { region = Region }));
    }
}
