using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ListeningHistoryResolutionTests
{
    [Theory]
    [InlineData("transport")]
    [InlineData("status")]
    [InlineData("size")]
    public async Task SwallowedFetchFailure_DefersWithoutReceiptAndNextInvocationCanRetry(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-history-fetch", Guid.NewGuid().ToString("N"));
        try
        {
            const string manifestJson = """
                {"id":"deezer","displayName":"Fixture","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
                 "capabilities":[{"kind":"Metadata","hooks":["searchTracks"],"accountScopes":[],"accountRequired":false}],"permissions":[]}
                """;
            var http = new HistoryFetchHandler(failure);
            var sandbox = new ExtensionSandbox(root, manifestJson, """
                registerExtension({searchTracks: function(request) {
                  try {
                    var response = fetch('https://catalog.example.test/search');
                    if (!response.ok) throw new Error('Search unavailable');
                  } catch (_) {}
                  return {items:[]};
                }});
                """, new HistoryFetchClientFactory(http), NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(["https://catalog.example.test/"]),
                    new HashSet<string>(), new HashSet<string>()), runtimeStateDirectory: root);
            var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, ExtensionSdkV1.ParseManifest(manifestJson));
            var context = new ProviderExecutionContext(new(ProviderActorKind.User, owner,
                new(Scope.Protocol, Scope.BackendInstanceId, "listener")), "deezer", null,
                new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                    ProviderExplicitContentPolicy.Allow, true, false, true, ["deezer"]),
                "history-search", "history-search", DateTimeOffset.UtcNow.AddMinutes(5), default);
            gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15)).Returns(async () =>
            {
                var outcome = await adapter.SearchTracksAsync(context, new("Song", new()));
                return outcome.IsSuccess ? ProviderOutcome<IReadOnlyList<Song>>.Success([]) :
                    ProviderOutcome<IReadOnlyList<Song>>.Failure(outcome.Error!);
            });
            await Add("Song");
            await Handler().RecoverAsync(default);

            Assert.Equal(DurableJobCompletionKind.Deferred, (await Run()).Kind);

            await using (var db = factory.CreateDbContext())
            {
                Assert.Empty(await db.ExternalMetadataSnapshots.ToArrayAsync());
                Assert.Equal(0, (await db.ListeningEvents.SingleAsync()).Revision);
                using var payload = JsonDocument.Parse((await db.Jobs.SingleAsync()).PayloadJson);
                Assert.Equal(0, payload.RootElement.GetProperty("Cursor").GetInt32());
            }
            http.Fail = false;
            clock.UtcNow += TimeSpan.FromMinutes(6);
            Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
            await using var read = factory.CreateDbContext();
            Assert.Single(await read.ExternalMetadataSnapshots.ToArrayAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class HistoryFetchClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class HistoryFetchHandler(string failure) : HttpMessageHandler
    {
        public bool Fail { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail && failure == "transport") throw new HttpRequestException("Search unavailable.");
            return Task.FromResult(new HttpResponseMessage(Fail && failure == "status" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(Fail && failure == "size" ? new string('x', 4 * 1024 * 1024 + 1) : "{}")
            });
        }
    }
}
