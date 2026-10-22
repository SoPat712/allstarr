using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Fact]
    public async Task CatalogPlaylistProbe_RemembersTheNativePlaylistWhenSearchResultsChange()
    {
        const string script = """
            var searches=0;
            registerExtension({customSearch:function(){
              http.get('https://catalog.example.test/search');
              return ++searches === 1 ? [{id:'playlist',name:'Classics',item_type:'playlist'}] : [];
            },getPlaylist:function(id){
              http.get('https://catalog.example.test/playlist/'+id);
              return {id:id,name:'Classics',tracks:[{id:'song',name:'Song'}]};
            }});
            """;
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, requests) =>
        {
            Assert.Equal(ProviderProbeStatus.Healthy, (await adapter.ProbeAsync(Context(manifest.Id),
                new(ProviderCapabilityKind.Playlist))).RequireValue().Status);
            requests.Clear();
            Assert.Equal(ProviderProbeStatus.Healthy, (await adapter.ProbeAsync(Context(manifest.Id),
                new(ProviderCapabilityKind.Playlist))).RequireValue().Status);
            Assert.Equal(["/playlist/playlist"], requests);
        });
    }

    [Fact]
    public async Task CatalogPlaylistProbe_TriesCuratedPlaylistsBeforeDiscovery()
    {
        const string script = """
            registerExtension({customSearch:function(query){
              http.get('https://catalog.example.test/search?q='+encodeURIComponent(query));
              if(!/classics|essentials|hits/i.test(query)) throw new Error('Expected a curated playlist query');
              return [{id:'playlist',name:'Classics',item_type:'playlist'}];
            },getPlaylist:function(id){return {id:id,tracks:[{id:'song',name:'Song'}]};}});
            """;
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, requests) =>
        {
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Playlist));
            Assert.Equal(ProviderProbeStatus.Healthy, result.RequireValue().Status);
            Assert.Single(requests);
        });
    }

    [Theory]
    [InlineData("throw new Error('HTTP 404');")]
    [InlineData("return {id:id,tracks:[{name:'Missing identifier'}]};")]
    public async Task CatalogPlaylistProbe_TriesTheNextNativeResultAfterAnUnusableSample(string missing)
    {
        var script = """
            registerExtension({customSearch:function(){
              return [1,2].map(function(id){return {id:String(id),name:'Playlist',item_type:'playlist'};});
            },getPlaylist:function(id){
              http.get('https://catalog.example.test/playlist/'+id);
              if(id === '1') { MISSING }
              return {id:id,name:'Playlist',tracks:[{id:'song',name:'Song'}]};
            }});
            """.Replace("MISSING", missing, StringComparison.Ordinal);
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, requests) =>
        {
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Playlist));
            Assert.Equal(ProviderProbeStatus.Healthy, result.RequireValue().Status);
            Assert.Equal(["/playlist/1", "/playlist/2"], requests);
        });
    }

    [Fact]
    public async Task CatalogPlaylistProbe_RegistersNativeDiscoveryWithoutSongSearch()
    {
        const string script = """
            registerExtension({getTrack:function(){},getPlaylist:function(id){
              http.get('https://catalog.example.test/playlist/'+id);
              return {id:id,name:'Playlist',tracks:[{id:'song',name:'Song'}]};
            }});
            """;
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, requests) =>
        {
            Assert.Contains("probePlaylist", Assert.Single(manifest.Capabilities,
                item => item.Kind == ProviderCapabilityKind.Health).Hooks);
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Playlist));
            Assert.Equal(ProviderProbeStatus.Healthy, result.RequireValue().Status);
            Assert.Equal(["/dj/djfind/djfind_inform.htm", "/playlist/901234567"], requests);
        }, "melon.com");
    }

    [Fact]
    public async Task CatalogProbe_RegistersSupportedLookupsWithoutServiceHealth()
    {
        const string script = """
            registerExtension({customSearch:function(){return [];},getTrack:function(){},getPlaylist:function(){},download:function(){}});
            """;
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, _) =>
        {
            var hooks = Assert.Single(manifest.Capabilities, item => item.Kind == ProviderCapabilityKind.Health).Hooks;
            Assert.Contains("probeMetadata", hooks);
            Assert.Contains("probePlaylist", hooks);
            Assert.Contains("probeDownload", hooks);
            Assert.DoesNotContain("probeStreaming", hooks);
            var unsupported = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Streaming));
            Assert.Equal(ProviderErrorKind.NotSupported, unsupported.Error?.Kind);
        });
    }

    [Theory]
    [InlineData(200, ProviderProbeStatus.Healthy, null)]
    [InlineData(401, ProviderProbeStatus.Unavailable, "authentication-required")]
    [InlineData(403, ProviderProbeStatus.Unavailable, "access-denied")]
    [InlineData(404, ProviderProbeStatus.Unavailable, "sample-unavailable")]
    [InlineData(429, ProviderProbeStatus.Unavailable, "rate-limited")]
    [InlineData(503, ProviderProbeStatus.Unavailable, "service_unavailable")]
    public async Task CatalogMetadataProbe_UsesSearchAndDetailAndPreservesHttpFailure(int status,
        ProviderProbeStatus expected, string? safeCode)
    {
        const string script = """
            registerExtension({searchTracks:function(query,limit){
              if(!query || limit !== 1) throw new Error('Unexpected search');
              var response=http.get('https://catalog.example.test/search');
              return response.ok ? {tracks:[{id:'native-track',name:'Get Lucky'}]} : {tracks:[]};
            },getTrack:function(id){
              if(id !== 'native-track') throw new Error('Unexpected detail');
              http.get('https://catalog.example.test/track/'+id); return {id:id,name:'Get Lucky'};
            }});
            """;
        await WithCatalogProbe(script, (HttpStatusCode)status, async (adapter, manifest, requests) =>
        {
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Metadata));
            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(expected, result.RequireValue().Status);
            Assert.Equal(safeCode, result.RequireValue().SafeCode);
            if (status == 200) Assert.Equal(["/search", "/track/native-track"], requests);
            else
            {
                Assert.All(requests, request => Assert.Equal("/search", request));
                Assert.InRange(requests.Count, status == 404 ? 8 : 2, status == 404 ? 16 : 3);
            }
        });
    }

    [Theory]
    [InlineData("return {id:id,name:'Hits',tracks:[{id:'track',name:'Song'}]};", ProviderProbeStatus.Healthy, null)]
    [InlineData("return null;", ProviderProbeStatus.Unavailable, "sample-unavailable")]
    [InlineData("return {id:id,name:'Hits',tracks:[]};", ProviderProbeStatus.Unavailable, "sample-unavailable")]
    [InlineData("return {id:id,name:'Hits',tracks:[{name:'Missing identifier'}]};", ProviderProbeStatus.Unavailable, "sample-unavailable")]
    public async Task CatalogPlaylistProbe_DiscoversAnIdentifierAndValidatesTracks(string detail,
        ProviderProbeStatus expected, string? safeCode)
    {
        var script = """
            registerExtension({customSearch:function(query,options){
              if(!query || options.filter !== 'playlist' || options.limit !== 3) throw new Error('Unexpected search');
              http.get('https://catalog.example.test/playlists');
              return [{id:'native-playlist',name:'Hits',item_type:'playlist'}];
            },getPlaylist:function(id){
              if(id !== 'native-playlist') throw new Error('Unexpected playlist identifier');
              http.get('https://catalog.example.test/playlist/'+id);
              DETAIL
            }});
            """.Replace("DETAIL", detail, StringComparison.Ordinal);
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, requests) =>
        {
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Playlist));
            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(expected, result.RequireValue().Status);
            Assert.Equal(safeCode, result.RequireValue().SafeCode);
            Assert.Equal(Enumerable.Range(0, expected == ProviderProbeStatus.Healthy ? 1 : safeCode == "sample-unavailable" ? 16 : 3)
                .SelectMany(_ => new[] { "/playlists", "/playlist/native-playlist" }), requests);
        });
    }

    [Fact]
    public async Task CatalogPlaylistProbe_DiscoversCurrentNativePlaylistsWhenSearchOnlyFindsSongs()
    {
        const string script = """
            registerExtension({customSearch:function(){throw new Error('This search only supports songs');},
              getPlaylist:function(id){
                if(id !== '901234567') throw new Error('Unexpected native sample');
                http.get('https://catalog.example.test/playlist/'+id);
                return {id:id,name:'Playlist',tracks:[{id:'track',name:'Song'}]};
              }});
            """;
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, requests) =>
        {
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Playlist));
            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(ProviderProbeStatus.Healthy, result.RequireValue().Status);
            Assert.Equal(["/dj/djfind/djfind_inform.htm", "/playlist/901234567"], requests);
        }, "melon.com");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    public async Task CatalogProbe_RemovedSamplesRetryThenDiscover(int missing, bool detailError)
    {
        var script = """
            var attempts=0;
            registerExtension({searchTracks:function(query,limit){
              var response=http.get('https://catalog.example.test/search');
              var attempt=JSON.parse(response.body).attempt;
              if(!DETAIL_ERROR && attempt <= MISSING) return {tracks:[]};
              return {tracks:[{id:'song-'+attempt,name:'Song'}]};
            },getTrack:function(id){
              if(DETAIL_ERROR && Number(id.slice(5)) <= MISSING) return {error:{code:404,message:'not found'}};
              return {id:id,name:'Song'};
            }});
            """.Replace("MISSING", missing.ToString(), StringComparison.Ordinal)
                .Replace("DETAIL_ERROR", detailError ? "true" : "false", StringComparison.Ordinal);
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, requests) =>
        {
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Metadata));
            Assert.Equal(ProviderProbeStatus.Healthy, result.RequireValue().Status);
            Assert.Equal(missing + 1, requests.Count);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogPlaylistProbe_ThreeDifferentFailedPlaylistsPreserveTheOutageReason(bool payloadError)
    {
        var script = """
            registerExtension({customSearch:function(){
              return [1,2,3].map(function(id){return {id:String(id),name:'Playlist',item_type:'playlist'};});
            },getPlaylist:function(id){http.get('https://catalog.example.test/playlist/'+id);return PAYLOAD;}});
            """.Replace("PAYLOAD", payloadError ? "{error:{code:503,message:'Service unavailable'}}" : "null", StringComparison.Ordinal);
        await WithCatalogProbe(script, payloadError ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, async (adapter, manifest, requests) =>
        {
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Playlist));
            Assert.Equal(ProviderProbeStatus.Unavailable, result.RequireValue().Status);
            Assert.Equal("service_unavailable", result.RequireValue().SafeCode);
            Assert.Equal(["/playlist/1", "/playlist/2", "/playlist/3"], requests);
        });
    }

    [Fact]
    public async Task CatalogProbe_StopsRepeatedRequestsEvenWhenTheSourceCatchesErrors()
    {
        const string script = """
            registerExtension({customSearch:function(){
              for(var i=0;i<20;i++) try {http.get('https://catalog.example.test/retry');} catch(_) {}
              return [{id:'native-track',name:'Song',item_type:'track'}];
            }});
            """;
        await WithCatalogProbe(script, HttpStatusCode.OK, async (adapter, manifest, requests) =>
        {
            var result = await adapter.ProbeAsync(Context(manifest.Id), new(ProviderCapabilityKind.Metadata));
            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(ProviderProbeStatus.Unavailable, result.RequireValue().Status);
            Assert.Equal("probe-limit-exceeded", result.RequireValue().SafeCode);
            Assert.InRange(requests.Count, 1, 8);
        });
    }

    private static async Task WithCatalogProbe(string script, HttpStatusCode status,
        Func<ExtensionHealthCapabilityAdapter, ExtensionSdkManifest, List<string>, Task> verify, string? nativeCatalog = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-catalog-probe", Guid.NewGuid().ToString("N"));
        try
        {
            var source = JsonSerializer.Serialize(new
            {
                name = "catalog-fixture",
                type = new[] { "metadata_provider", "download_provider" },
                permissions = new { network = nativeCatalog == null ? new[] { "catalog.example.test" } : new[] { "catalog.example.test", nativeCatalog } }
            });
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var requests = new List<string>();
            var handler = new AppleProviderTestFactory.Handler(request =>
            {
                requests.Add(request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                    request.RequestUri.Host == "www.melon.com" ? "<a href=\"?plylstSeq=901234567\">Playlist</a>" : JsonSerializer.Serialize(new { attempt = requests.Count }))
                };
            });
            var sandbox = new ExtensionSandbox(root, normalized, script, new HttpClientFactory(handler), NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(nativeCatalog == null ? ["https://catalog.example.test/"] : ["https://catalog.example.test/", $"https://www.{nativeCatalog}/"]),
                    new HashSet<string>(), new HashSet<string>()), Path.Combine(root, "runtime"));
            await verify(new ExtensionHealthCapabilityAdapter(sandbox, manifest), manifest, requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
