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
                new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 4096 });
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
                  total_tracks:9,release_date:'2026-01-02',genre:'Rock',label:'Fixture',album_artist:'Artist',bitrate:320});
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
        Assert.Equal("2026-01-02", track.ReleaseDate);
        Assert.Equal("Rock", track.Genre);
        Assert.Equal("Fixture", track.Label);
        Assert.Equal("Artist", track.AlbumArtist);
        Assert.Equal(320, track.Bitrate);
        sandbox.InvokeJson("searchTracks", """{"query":"Song"}""");
        Assert.Equal("USFIX2600001", (await adapter.GetTrackAsync(Context(manifest.Id), request)).RequireValue().Isrc);
        using var counter = JsonDocument.Parse(sandbox.InvokeJson("getArtist", """{"id":"counter"}""")!);
        Assert.Equal("1", counter.RootElement.GetProperty("id").GetString());
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
