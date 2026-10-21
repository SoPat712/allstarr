using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Jint.Runtime;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed class ExtensionExecutionLimitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-execution-tests", Guid.NewGuid().ToString("N"));
    private readonly CaptureLogger logger = new();
    private readonly List<(string Level, string Message)> events = [];

    [Fact]
    public void SearchTracks_ParsesAndTraversesLargeNestedResponse()
    {
        var payload = JsonSerializer.Serialize(new
        {
            padding = new string('x', 550_000),
            methods = Enumerable.Range(0, 600).Select(index => new
            {
                header = new { text = "Songs", details = new { label = "Catalog results", position = index } },
                items = Enumerable.Range(0, 8).Select(child => new
                {
                    kind = "track",
                    id = $"track-{index}-{child}",
                    primary = new { text = $"Song {index}-{child}", artist = "Fixture Artist" },
                    secondary = new { duration = "3:21", labels = new[] { "Catalog", "Music", "Stereo" } }
                })
            })
        });
        Assert.InRange(Encoding.UTF8.GetByteCount(payload), 1_000_000, 2_000_000);
        const string source = """
            {"name":"fixture","version":"1.0.0","type":["metadata_provider"],
             "permissions":{"network":["catalog.example.test"]},
             "searchBehavior":{"enabled":true,"filters":[{"id":"songs","label":"Songs"}]}}
            """;
        const string script = """
            function find(node, kind, matches, depth) {
              if (!node || typeof node !== 'object' || depth > 20) return matches;
              if (node.kind === kind) matches.push(node);
              for (var key in node) find(node[key], kind, matches, depth + 1);
              return matches;
            }
            registerExtension({customSearch: function(query, options) {
              if (options.filter !== 'songs') throw new Error('Unexpected search filter');
              var data = fetch('https://catalog.example.test/search').json();
              var tracks = find(data, 'track', [], 0);
              find(data, 'album', [], 0);
              find(data, 'artist', [], 0);
              var encoded = data.methods.map(function(section) { return JSON.stringify(section); });
              if (encoded.length !== 600 || tracks.length !== 4800) throw new Error('Incomplete response');
              return tracks.slice(0, options.limit).map(function(track) {
                return {id:track.id, name:track.primary.text, artists:track.primary.artist, item_type:'track'};
              });
            }});
            """;
        Directory.CreateDirectory(root);
        var factory = new FixtureFactory(payload);
        var sandbox = new ExtensionSandbox(root, SpotiFlacExtensionCompatibility.NormalizeManifest(source, script),
            script, factory, logger,
            new ExtensionRuntimePermissionSet(new HashSet<string>(["https://catalog.example.test/"]), new HashSet<string>(), new HashSet<string>()),
            runtimeStateDirectory: root);

        using var result = JsonDocument.Parse(sandbox.InvokeJson("searchTracks", """{"query":"fixture","page":{"limit":15}}""")!);

        var items = result.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(15, items.Length);
        Assert.Equal("Song 0-0", items[0].GetProperty("title").GetString());
        Assert.Equal("Song 1-6", items[^1].GetProperty("title").GetString());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void InvokeJson_InfiniteLoopStopsAtDeadlineAndLogsOneSafeWarning()
    {
        var sandbox = Create("""
            registerExtension({searchTracks: function(request) {
              if (request.query === 'complete') return {items:[]};
              try { while (true) {} } catch (_) { return {items:[]}; }
            }});
            """, TimeSpan.FromMilliseconds(200));
        var elapsed = Stopwatch.StartNew();

        var error = Assert.Throws<TimeoutException>(() => sandbox.InvokeJson("searchTracks", """{"query":"private-request-value"}"""));
        sandbox.LogDownloadFailure(error);

        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3));
        AssertWarning("time");
        Assert.Equal("{\"items\":[]}", sandbox.InvokeJson("searchTracks", """{"query":"complete"}"""));
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void InvokeJson_RecursionLimitLogsOneSafeWarning()
    {
        var sandbox = Create("""
            function recurse(value) { return recurse(value); }
            registerExtension({searchTracks: function(request) { return recurse(request.query); }});
            """);

        var error = Assert.Throws<RecursionDepthOverflowException>(() => sandbox.InvokeJson("searchTracks", """{"query":"private-request-value"}"""));
        sandbox.LogDownloadFailure(error);

        AssertWarning("recursion");
    }

    [Fact]
    public void InvokeJson_CallerCancellationDoesNotLogLimitWarning()
    {
        var sandbox = Create("registerExtension({searchTracks: function() { while (true) {} }});");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => sandbox.InvokeJson("searchTracks", "{}", cancellation.Token));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void InvokeJson_AllocationLimitLogsOneSafeWarning()
    {
        var sandbox = Create("""
            registerExtension({searchTracks: function(request) {
              var text = JSON.stringify(Array(1000).fill({title:request.query, children:[1,2,3]}));
              for (var index = 0; index < 5000; index++) JSON.parse(text);
              return {items:[]};
            }});
            """);

        Assert.Throws<MemoryLimitExceededException>(() => sandbox.InvokeJson("searchTracks", """{"query":"private-request-value"}"""));

        AssertWarning("memory");
    }

    private ExtensionSandbox Create(string script, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(root);
        return new ExtensionSandbox(root, """{"id":"fixture","version":"1.0.0"}""", script,
            new Mock<IHttpClientFactory>(MockBehavior.Strict).Object, logger,
            new ExtensionRuntimePermissionSet(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
                LogSink: (level, message) => events.Add((level, message))),
            runtimeStateDirectory: root, hookTimeout: timeout);
    }

    private void AssertWarning(string limit)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("fixture", entry.State["ExtensionId"]);
        Assert.Equal("searchTracks", entry.State["Operation"]);
        Assert.Equal(limit, entry.State["Limit"]);
        Assert.DoesNotContain("private-request-value", entry.Message, StringComparison.Ordinal);
        var retained = Assert.Single(events);
        Assert.Equal("warn", retained.Level);
        Assert.Equal(entry.Message, retained.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FixtureFactory(string payload) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FixtureHandler(payload));
    }

    private sealed class FixtureHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, string Message, Dictionary<string, object?> State)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                Entries.Add((logLevel, exception, formatter(state, exception),
                    ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(item => item.Key, item => item.Value)));
        }
    }
}
