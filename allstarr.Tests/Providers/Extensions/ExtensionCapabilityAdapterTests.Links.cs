using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Health;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Fact]
    public async Task PlaylistLookup_OperatesWithoutDiscoveryOrAnAccount()
    {
        const string script = """
            registerExtension({getPlaylist:function(id) {
              return {id:id,name:'Collection',artists:'Listener',tracks:[{id:'song-1',name:'Song',artists:'Artist'}]};
            }});
            """;
        var (sandbox, manifest) = CompatibilitySandbox(script);
        var capability = Assert.Single(manifest.Capabilities);
        Assert.Equal(ProviderCapabilityKind.Playlist, capability.Kind);
        Assert.False(capability.SupportsPlaylistDiscovery);
        Assert.Equal(new[] { "getPlaylistTracks" }, capability.Hooks);
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);

        var result = await adapter.GetPlaylistTracksAsync(Context(manifest.Id), new(
            new(manifest.Id, ProviderResourceKind.Playlist, "list-1"), new(50)));

        var page = result.RequireValue();
        Assert.Equal("list-1", page.Playlist.Id.Value);
        Assert.Null(page.Playlist.Description);
        Assert.Equal("song-1", Assert.Single(page.Tracks.Items).TrackId.Value);
        Assert.Equal(ProviderErrorKind.NotSupported, (await adapter.GetUserPlaylistsAsync(
            Context(manifest.Id), new(new(50)))).Error!.Kind);
    }

    [Fact]
    public async Task PlaylistLookup_PagesTheReturnedTracksForImportsAndPreviews()
    {
        const string script = """
            registerExtension({getPlaylist:function(id) {
              return {id:id,name:'Collection',tracks:[{id:'one'},{id:'two'},{id:'three'}]};
            }});
            """;
        var (sandbox, manifest) = CompatibilitySandbox(script);
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);
        var resource = new ProviderExternalResourceId(manifest.Id, ProviderResourceKind.Playlist, "list");
        var first = (await adapter.GetPlaylistTracksAsync(Context(manifest.Id), new(resource, new(2)))).RequireValue();
        Assert.Equal(3, first.Playlist.TrackCount);
        Assert.Equal(new[] { "one", "two" }, first.Tracks.Items.Select(item => item.TrackId.Value));
        Assert.Equal("2", first.Tracks.NextCursor);
        var last = (await adapter.GetPlaylistTracksAsync(Context(manifest.Id), new(resource, new(2, first.Tracks.NextCursor)))).RequireValue();
        Assert.Equal(2, Assert.Single(last.Tracks.Items).Position);
        Assert.Null(last.Tracks.NextCursor);
    }

    [Fact]
    public async Task PlaylistLookup_ProxiesDeclaredArtworkWithTheExistingImageBounds()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z0i8AAAAASUVORK5CYII=");
        var handler = new BytesHandler(bytes, "image/png");
        const string script = """
            registerExtension({getPlaylist:function(id){return {id:id,name:'Mix',cover_url:'https://images.example.test/cover.png',tracks:[]};}});
            """;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
            """{"name":"fixture","version":"1.0.0","type":["metadata_provider"]}""", script);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        var permissions = new ExtensionRuntimePermissionSet(new HashSet<string>(["https://images.example.test/"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), normalized, script, new HttpClientFactory(handler), NullLogger.Instance, permissions);
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);
        var resource = new ProviderExternalResourceId(manifest.Id, ProviderResourceKind.Playlist, "list");
        var artwork = (await adapter.ResolveArtworkAsync(Context(manifest.Id), new(new(resource, revision: "1")))).RequireValue();
        Assert.Equal(bytes, artwork.Bytes);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task UrlLookup_RetainsAnOpaqueShareLinkWhenOnlyExpandedPlaylistTracksAreReturned()
    {
        const string script = """
            registerExtension({
              getPlaylist:function(){throw new Error('Expected the shared link');},
              handleUrl:function(url){return {type:'playlist',name:'Shared mix',tracks:[{id:'song',name:'Song',artists:'Artist'}]};}
            });
            """;
        var (sandbox, manifest) = CompatibilitySandbox(script);
        manifest = manifest with { UrlPatterns = ["music.example.test"] };
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);
        const string url = "https://music.example.test/shared/token";
        var resource = (await adapter.ResolveUrlAsync(Context(manifest.Id), url)).RequireValue();
        Assert.Equal(url, resource.Value);
        var page = (await adapter.GetPlaylistTracksAsync(Context(manifest.Id), new(resource, new(1)))).RequireValue();
        Assert.Equal(resource, page.Playlist.Id);
        Assert.Equal("Shared mix", page.Playlist.Name);
        Assert.Equal("song", Assert.Single(page.Tracks.Items).TrackId.Value);
    }

    [Fact]
    public async Task PlaylistLookup_PreservesOpaqueIdsContainingAColon()
    {
        const string script = """
            registerExtension({getPlaylist:function(id){return {id:id,name:'Mix',tracks:[]};},
              handleUrl:function(){throw new Error('Expected a playlist ID');}});
            """;
        var (sandbox, manifest) = CompatibilitySandbox(script);
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);
        var resource = new ProviderExternalResourceId(manifest.Id, ProviderResourceKind.Playlist, "opaque:list");
        var page = (await adapter.GetPlaylistTracksAsync(Context(manifest.Id), new(resource, new(1)))).RequireValue();
        Assert.Equal(resource, page.Playlist.Id);
    }

    [Fact]
    public void StoredCompatibilityManifest_ReconcilesPlaylistLookupAndDeclaredUrls()
    {
        const string source = """
            {"name":"fixture","version":"1.0.0","type":["metadata_provider"],
             "capabilities":{"urlPatterns":["^https://music.example.test/"]}}
            """;
        const string script = "registerExtension({getTrack:function(){},getPlaylist:function(){},handleUrl:function(){}});";
        var stored = JsonNode.Parse(SpotiFlacExtensionCompatibility.NormalizeManifest(source, script))!.AsObject();
        stored["capabilities"] = new JsonArray(JsonNode.Parse("""
            {"kind":"Metadata","hooks":["getTrack"],"accountScopes":[],"accountRequired":false}
            """));
        stored.Remove("urlPatterns");

        var manifest = ExtensionSdkV1.ParseManifest(SpotiFlacExtensionCompatibility.ReconcileMetadataHooks(stored.ToJsonString(), script));

        Assert.Contains(manifest.Capabilities, item => item.Kind == ProviderCapabilityKind.Playlist && !item.SupportsPlaylistDiscovery);
        Assert.Equal(new[] { "^https://music.example.test/" }, manifest.UrlPatterns);
    }

    [Fact]
    public async Task UrlLookup_UsesTheDeclaredHookAndRetainsProviderOwnership()
    {
        const string source = """
            {"name":"fixture","version":"1.0.0","type":["metadata_provider"],
             "urlHandler":{"enabled":true,"patterns":["music.example.test","fixture:"]}}
            """;
        const string script = """
            var calls = 0;
            registerExtension({
              getTrack:function(){return {id:'song-1',name:'Song',artists:'Artist'};},
              handleUrl:function(url){calls++; if(calls !== 1 || url !== 'https://music.example.test/list-1') throw new Error('unexpected call');
                return {type:'playlist',id:'list-1',provider_id:'other'};}
            });
            """;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), normalized, script, new HttpClientFactory(),
            NullLogger.Instance, ExtensionRuntimePermissionSet.None);
        var observer = new Mock<IProviderOutcomeObserver>();
        var wrapped = ObservedProviderCapabilities.Wrap<IProviderMetadataCapability>(
            new ExtensionMetadataCapabilityAdapter(sandbox, manifest), observer.Object);
        var adapter = Assert.IsAssignableFrom<IProviderUrlCapability>(wrapped);

        Assert.Equal(new[] { "music.example.test", "fixture:" }, adapter.UrlPatterns);
        var resource = (await adapter.ResolveUrlAsync(Context(manifest.Id), "https://music.example.test/list-1")).RequireValue();
        Assert.Equal(manifest.Id, resource.ProviderId);
        Assert.Equal(ProviderResourceKind.Playlist, resource.ResourceKind);
        Assert.Equal("list-1", resource.Value);
        observer.Verify(item => item.Observe(It.IsAny<ProviderExecutionContext>(), ProviderCapabilityKind.Metadata,
            null, It.IsAny<TimeSpan>()), Times.Once);
    }

    [Theory]
    [InlineData("{type:'playlist',name:'Mix',tracks:[]}", "https://music.example.test/browse/playlist/list-1", ProviderResourceKind.Playlist, "list-1")]
    [InlineData("{type:'playlist',name:'Mix',tracks:[]}", "https://music.example.test/playlists/list-1", ProviderResourceKind.Playlist, "list-1")]
    [InlineData("{type:'playlist',name:'Mix',tracks:[]}", "tidal:playlist:list-1", ProviderResourceKind.Playlist, "list-1")]
    [InlineData("{type:'playlist',name:'Mix',tracks:[]}", "tidal://playlist/list-1", ProviderResourceKind.Playlist, "list-1")]
    [InlineData("{type:'playlist',playlist:{id:'list-1'},tracks:[]}", "https://music.example.test/shared/link", ProviderResourceKind.Playlist, "list-1")]
    [InlineData("{type:'album',album:{id:'album-1'},tracks:[]}", "https://music.example.test/shared/link", ProviderResourceKind.Album, "album-1")]
    [InlineData("{type:'track',track:{id:123}}", "https://music.example.test/shared/link", ProviderResourceKind.Track, "123")]
    public async Task UrlLookup_AcceptsEmbeddedResourcesAndConfirmedResourcePaths(string response, string url, ProviderResourceKind kind, string id)
    {
        var (sandbox, manifest) = CompatibilitySandbox("registerExtension({getTrack:function(){},handleUrl:function(){return " + response + ";}});");
        manifest = manifest with { UrlPatterns = ["music.example.test"] };
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var result = (await adapter.ResolveUrlAsync(Context(manifest.Id), url)).RequireValue();
        Assert.Equal(kind, result.ResourceKind);
        Assert.Equal(id, result.Value);
    }

    [Theory]
    [InlineData("null", ProviderErrorKind.NotFound)]
    [InlineData("{success:false,type:'playlist',id:'item'}", ProviderErrorKind.NotFound)]
    [InlineData("{type:'unsupported',id:'item'}", ProviderErrorKind.NotSupported)]
    [InlineData("{type:'playlist',id:''}", ProviderErrorKind.PermanentFailure)]
    public async Task UrlLookup_RejectsUnrecognizedOrInvalidResults(string response, ProviderErrorKind expected)
    {
        var (sandbox, manifest) = CompatibilitySandbox("registerExtension({getTrack:function(){},handleUrl:function(){return " + response + ";}});");
        manifest = manifest with { UrlPatterns = ["music.example.test"] };
        var adapter = Assert.IsAssignableFrom<IProviderUrlCapability>(new ExtensionMetadataCapabilityAdapter(sandbox, manifest));

        Assert.Equal(expected, (await adapter.ResolveUrlAsync(Context(manifest.Id), "https://music.example.test/item")).Error!.Kind);
    }

    [Fact]
    public void NativePlaylistDiscovery_DefaultsToEnabled()
    {
        var manifest = Manifest(ProviderCapabilityKind.Playlist, "getUserPlaylists", "getPlaylistTracks");
        Assert.True(Assert.Single(manifest.Capabilities).SupportsPlaylistDiscovery);
        Assert.True(new ProviderCapabilityDescriptor(ProviderCapabilityKind.Playlist,
            ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required, "1",
            ["getUserPlaylists", "getPlaylistTracks"], [ProviderAccountScope.Personal]).SupportsPlaylistDiscovery);
    }

    [Fact]
    public void ReadOnlyPlaylistManifest_AllowsExplicitPublicReadWithoutDiscovery()
    {
        var descriptor = new ProviderDescriptor("fixture-extension", "Fixture", "Fixture", ProviderOrigin.Extension,
            "1", "1", [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getPlaylistTracks"],
                supportsPublicRead: true, supportsPlaylistDiscovery: false)], new(), entryPoint: "index.js");

        Assert.Same(descriptor, ProviderManifestValidator.Validate(descriptor));
    }
}
