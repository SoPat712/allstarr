using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ExtensionControlPlaneServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogChecks_RepairStoredDeclarationsAndReplaceStaleResultsOnTheNextSweep(bool media)
    {
        var script = """
            function search(query, options) {
              var response = fetch('https://catalog.example.test/search', {method:'POST',body:JSON.stringify({query:query})});
              var attempt = response.json().attempt;
              if (attempt <= 8) return [];
              return [{id:options && options.filter === 'playlist' ? 'playlist' : 'song',name:'Classics',item_type:options && options.filter === 'playlist' ? 'playlist' : 'track'}];
            }
            function track(id) {
              http.get('https://catalog.example.test/track/'+id);
              return {id:id,name:'Song',artists:'Artist',duration_ms:200000};
            }
            function playlist(id) {
              http.get('https://catalog.example.test/playlist/'+id);
              return {id:id,name:'Classics',tracks:[{id:'song',name:'Song',artists:'Artist'}]};
            }
            registerExtension({customSearch:search,getTrack:track,getPlaylist:playlist MEDIA});
            """.Replace("MEDIA", media ? """
                ,checkAvailability:function(){return {available:true,track_id:'song'};},
                download:function(id,quality,path){
                  var result=file.download('https://catalog.example.test/audio.flac',path+'.flac');
                  return {success:result.success,file_path:result.path,format:'flac'};
                }
                """ : "", StringComparison.Ordinal);
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(JsonSerializer.Serialize(new
        {
            name = "catalog-fixture",
            version = "1.0.0",
            type = media ? new[] { "metadata_provider", "download_provider" } : ["metadata_provider"],
            permissions = new { network = new[] { "catalog.example.test", media ? "music.youtube.com" : "www.melon.com" } }
        }), script);
        var stored = JsonNode.Parse(normalized)!.AsObject();
        var capabilities = stored["capabilities"]!.AsArray();
        foreach (var declaration in capabilities.OfType<JsonObject>().Where(item =>
            item["kind"]!.GetValue<string>() is "Health" or "Metadata").ToArray()) capabilities.Remove(declaration);
        var content = Package("1.0.0", "catalog-checks");
        File.WriteAllText(Path.Combine(content.PackageRoot, "manifest.json"), stored.ToJsonString());
        File.WriteAllText(Path.Combine(content.PackageRoot, "index.js"), script);
        content = content with
        {
            Manifest = ExtensionSdkV1.ParseManifest(stored.ToJsonString()),
            ContentSha256 = ExtensionSdkV1.ComputePackageContentSha256(content.PackageRoot)
        };
        var package = await _service.StageAsync(content);
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [new("network", "https://catalog.example.test/", true),
                new("network", media ? "https://music.youtube.com/" : "https://www.melon.com/", true)]);
        var handler = new CatalogCheckHandler();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var registry = new ProviderRegistry([]);
        var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "downloads") };
        await using var runtime = new ExtensionRuntimeCoordinator(_factory, _service, registry, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), options, audioVerifier: FixtureAudioVerifier.Instance), options,
            _configuration, NullLogger<ExtensionRuntimeCoordinator>.Instance);
        await runtime.ActivateAsync(package.Id, package.Revision);

        var expected = media ? new[] { "metadata", "playlist", "streaming", "download" } : ["metadata", "playlist"];
        using var services = new ServiceCollection().AddSingleton<IProviderRegistry>(registry)
            .AddSingleton(new ProviderDiagnosticExecutionFactory(registry, Mock.Of<IProviderRouteAccountResolver>()))
            .AddSingleton(new ProviderTryOperations(registry)).BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ProviderChecks:PauseMilliseconds"] = "0" }).Build();
        var status = new ProviderStatusManager(config, clients.Object, NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()), services: services);
        var history = new ProviderCheckHistory(_factory, TimeProvider.System);
        foreach (var capability in expected)
        {
            Assert.True(status.CanTestAccountFreeCapability(package.ExtensionId, capability), capability);
            await history.RecordAsync(new(ProviderRuntimeStatusKey.CreateAccountFree(package.ExtensionId, capability), null,
                new ProviderRuntimeStatus
                {
                    Provider = package.ExtensionId,
                    Capability = capability,
                    IsSupported = true,
                    IsEnabled = true,
                    Configuration = ProviderConfigurationState.NotRequired,
                    Health = ProviderHealthState.Degraded,
                    TestedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    ReasonCode = "probe_not_available"
                }), "previous-check", default);
        }
        using var selector = new ProviderCtsTrackSelector(_factory);
        var measurement = new ProviderCtsDiagnosticRunner(registry, Mock.Of<IProviderRouteAccountResolver>(), selector,
            new ProviderRuntimeHealth(), _factory, statusManager: status);
        var runner = new ProviderAccountCheckRunner(_factory, status, history, config, TimeProvider.System,
            NullLogger<ProviderAccountCheckRunner>.Instance, registry: registry, media: measurement);
        await runner.RestoreAsync(default);
        Assert.True(await runner.SweepAsync(default, package.ExtensionId));
        var results = (await history.ReadLatestAsync(default)).Where(item => item.Key.Provider == package.ExtensionId).ToArray();
        Assert.Equal(expected.Order(), results.Select(item => item.Key.Capability).Order());
        Assert.All(results, item =>
        {
            Assert.Equal(ProviderHealthState.Healthy, item.Status.Health);
            Assert.Null(item.Status.ReasonCode);
            Assert.True(item.Status.TestedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.True(item.Status.HasCheckResult);
        });
        Assert.InRange(handler.Searches, 9, 30);
        Assert.Contains("/track/song", handler.Paths);
        Assert.Contains(media ? "/playlist/playlist" : "/playlist/901234567", handler.Paths);
        if (media)
        {
            Assert.Contains("/audio.flac", handler.Paths);
            var download = Assert.Single(results, item => item.Key.Capability == "download").Status;
            Assert.NotNull(download.VerificationMeasurementId);
            Assert.Equal("streaming-download", download.VerificationMethod);
        }
        else Assert.DoesNotContain("/audio.flac", handler.Paths);
    }

    private sealed class CatalogCheckHandler : HttpMessageHandler
    {
        public int Searches { get; private set; }
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/search") Searches++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = path == "/audio.flac" ? new ByteArrayContent([102, 76, 97, 67]) : new StringContent(
                    request.RequestUri.Host == "www.melon.com" ? "<a href='?plylstSeq=901234567'>Classics</a>" :
                    JsonSerializer.Serialize(new { attempt = Searches }))
            });
        }
    }
}
