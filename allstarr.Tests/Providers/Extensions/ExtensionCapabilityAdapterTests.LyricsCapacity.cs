using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Fact]
    public async Task LyricsHookBudgetExpiryIsLocalCapacityRatherThanAnUpstreamFailure()
    {
        var manifest = Manifest(ProviderCapabilityKind.Lyrics, "fetchLyrics");
        var clock = new ManualTimeProvider();
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), JsonSerializer.Serialize(new
        { id = manifest.Id, displayName = "Fixture", version = "1.0.0" }),
            "registerExtension({fetchLyrics:function(){utils.sleep(2000);return {availability:'Unavailable',source:'fixture'};}});",
            new HttpClientFactory(), NullLogger.Instance, hookTimeout: TimeSpan.FromSeconds(1), timeProvider: clock);
        var adapter = new ExtensionLyricsCapabilityAdapter(sandbox, manifest);
        var pending = adapter.FetchLyricsAsync(Context(manifest.Id), new ProviderLyricsRequest(Guid.NewGuid(),
            new(manifest.Id, ProviderResourceKind.Track, "track"), trackTitle: "Fixture", artistNames: ["Artist"]));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("local-capacity-unavailable", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Error?.Code);
    }
}
