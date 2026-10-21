using System.Text.Json;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed partial class ExtensionControlPlaneServiceTests
{
    [Theory]
    [InlineData("stop")]
    [InlineData("disable")]
    [InlineData("replace")]
    public async Task RuntimeUnload_CallsSourceCleanupOnceWithoutReinitializing(string operation)
    {
        var content = Package("1.0.0", "lifecycle");
        var script = """
            var initialized = 0;
            registerExtension({initialize:function(){initialized++;},getTrack:function(){},cleanup:function(){
              storage.set('closed', {count:(storage.get('closed', {count:0}).count + 1),initialized:initialized});
            }});
            """;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
            """{"name":"fixture","version":"1.0.0","type":["metadata_provider"],"permissions":{"storage":true}}""", script);
        File.WriteAllText(Path.Combine(content.PackageRoot, "manifest.json"), normalized);
        File.WriteAllText(Path.Combine(content.PackageRoot, "index.js"), script);
        content = content with { Manifest = ExtensionSdkV1.ParseManifest(normalized), ContentSha256 = ExtensionSdkV1.ComputePackageContentSha256(content.PackageRoot) };
        var package = await _service.StageAsync(content);
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision, [new("cache", "*", true)]);
        var coordinator = Coordinator(new SignedSessionHandler());
        package = await coordinator.ActivateAsync(package.Id, package.Revision);
        if (operation == "disable") await coordinator.DisableAsync(package.Id, package.Revision);
        else if (operation == "replace") await coordinator.StartAsync(default);
        else await coordinator.StopAsync(default);

        var storage = Path.Combine(_root, "extensions", ".runtime", "spotiflac-fixture", "storage.json");
        Assert.True(File.Exists(storage));
        using var saved = JsonDocument.Parse(File.ReadAllText(storage));
        var json = saved.RootElement.GetProperty("closed").GetString()!;
        using var value = JsonDocument.Parse(json[6..]);
        Assert.Equal(1, value.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, value.RootElement.GetProperty("initialized").GetInt32());
        if (operation == "stop")
        {
            await coordinator.StopAsync(default);
            using var repeated = JsonDocument.Parse(File.ReadAllText(storage));
            Assert.Equal(saved.RootElement.GetRawText(), repeated.RootElement.GetRawText());
        }
    }
}
