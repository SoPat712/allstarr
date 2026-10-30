using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Fact]
    public async Task ExtensionHealthLogging_ReportsTransitionsAndHourlySummary()
    {
        var clock = new ManualTimeProvider();
        var logger = new ProbeLogger();
        var diagnostics = new List<string>();
        var manifest = Manifest(ProviderCapabilityKind.Health, "probeStreaming");
        var sandbox = new ExtensionSandbox(Path.GetTempPath(),
            """{"id":"probe-logging","version":"1.0.0"}""", """
            var status = 'Healthy', sequence = 0;
            registerExtension({
              configure:function(request){status=request.status;},
              probeStreaming:function(){
                if(status !== 'Healthy') {
                  log.error('Search failed for sample ' + (++sequence));
                  log.warn('Retry failed for sample ' + sequence);
                }
                return {status:status,observedAt:'2030-01-01T00:00:00Z',safeCode:'service_unavailable'};
              }
            });
            """, new HttpClientFactory(), logger,
            new ExtensionRuntimePermissionSet(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
                LogSink: (_, message) => diagnostics.Add(message)),
            timeProvider: clock);
        var adapter = new ExtensionHealthCapabilityAdapter(sandbox, manifest);
        async Task Check(ProviderProbeStatus expected)
        {
            var result = await adapter.ProbeAsync(Context(), new(ProviderCapabilityKind.Streaming));
            Assert.True(result.IsSuccess);
            Assert.Equal(expected, result.RequireValue().Status);
        }

        await Check(ProviderProbeStatus.Healthy);
        Assert.Empty(logger.Events);
        sandbox.InvokeJson("configure", """{"status":"Unavailable"}""");
        await Check(ProviderProbeStatus.Unavailable);
        await Check(ProviderProbeStatus.Unavailable);
        clock.Advance(TimeSpan.FromMinutes(59));
        await Check(ProviderProbeStatus.Unavailable);
        Assert.Single(logger.Events);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Check(ProviderProbeStatus.Unavailable);
        Assert.Equal(2, logger.Events.Count);
        Assert.Contains("3 repeated failures", logger.Events[1].Message);
        await Check(ProviderProbeStatus.Unavailable);
        sandbox.InvokeJson("configure", """{"status":"Healthy"}""");
        await Check(ProviderProbeStatus.Healthy);
        await Check(ProviderProbeStatus.Healthy);
        sandbox.InvokeJson("configure", """{"status":"Degraded"}""");
        await Check(ProviderProbeStatus.Degraded);

        Assert.Equal([LogLevel.Warning, LogLevel.Warning, LogLevel.Information, LogLevel.Warning],
            logger.Events.Select(item => item.Level));
        Assert.Contains(logger.Events, item => item.Message.Contains("recovered", StringComparison.Ordinal));
        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, message => message.StartsWith("Search failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtensionHealthLogging_SeparatesCapabilitiesAndAccounts()
    {
        var logger = new ProbeLogger();
        var manifest = Manifest(ProviderCapabilityKind.Health, "probeStreaming", "probeDownload");
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), """{"id":"probe-isolation"}""", """
            function fail(){return {status:'Unavailable',observedAt:'2030-01-01T00:00:00Z',safeCode:'timeout'};}
            registerExtension({probeStreaming:fail,probeDownload:fail});
            """, new HttpClientFactory(), logger);
        var adapter = new ExtensionHealthCapabilityAdapter(sandbox, manifest);
        var first = Context(includeAccount: true);
        var second = Context(includeAccount: true);

        foreach (var context in new[] { first, second })
            foreach (var capability in new[] { ProviderCapabilityKind.Streaming, ProviderCapabilityKind.Download })
            {
                await adapter.ProbeAsync(context, new(capability));
                await adapter.ProbeAsync(context, new(capability));
            }

        Assert.Equal(4, logger.Events.Count);
        Assert.All(logger.Events, item => Assert.Equal(LogLevel.Warning, item.Level));
    }

    [Fact]
    public async Task ExtensionHealthLogging_CanceledAndUnsupportedChecksDoNotRecoverFailures()
    {
        var logger = new ProbeLogger();
        var manifest = Manifest(ProviderCapabilityKind.Health, "probeStreaming");
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), """{"id":"probe-exception"}""",
            "registerExtension({probeStreaming:function(){throw new Error('upstream failed');}});",
            new HttpClientFactory(), logger);
        var adapter = new ExtensionHealthCapabilityAdapter(sandbox, manifest);

        var failure = await adapter.ProbeAsync(Context(), new(ProviderCapabilityKind.Streaming));
        Assert.False(failure.IsSuccess);
        var unsupported = await adapter.ProbeAsync(Context(), new(ProviderCapabilityKind.Download));
        Assert.Equal(ProviderErrorKind.NotSupported, unsupported.Error?.Kind);
        var canceled = await adapter.ProbeAsync(ProviderDiagnosticSamples.WithCancellation(Context(), new(true)),
            new(ProviderCapabilityKind.Streaming));
        Assert.Equal(ProviderErrorKind.Canceled, canceled.Error?.Kind);
        await adapter.ProbeAsync(Context(), new(ProviderCapabilityKind.Streaming));

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Events).Level);
    }

    [Fact]
    public async Task ExtensionHealthLogging_RetainsTransportDiagnosticsWithoutRepeatedWarnings()
    {
        var logger = new ProbeLogger();
        var diagnostics = new List<string>();
        var manifest = Manifest(ProviderCapabilityKind.Health, "probeStreaming");
        var handler = new AppleProviderTestFactory.Handler(_ => throw new HttpRequestException("offline"));
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), """{"id":"probe-transport"}""", """
            registerExtension({probeStreaming:function(){
              http.get('https://health.example.test/');
              return {status:'Unavailable',observedAt:'2030-01-01T00:00:00Z',safeCode:'unreachable'};
            }});
            """, new HttpClientFactory(handler), logger,
            new ExtensionRuntimePermissionSet(new HashSet<string>(["https://health.example.test/"]),
                new HashSet<string>(), new HashSet<string>(), LogSink: (_, message) => diagnostics.Add(message)));
        var adapter = new ExtensionHealthCapabilityAdapter(sandbox, manifest);

        for (var i = 0; i < 2; i++)
        {
            var result = await adapter.ProbeAsync(Context(), new(ProviderCapabilityKind.Streaming));
            Assert.True(result.IsSuccess);
            Assert.Equal(ProviderProbeStatus.Unavailable, result.RequireValue().Status);
        }

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Events).Level);
        Assert.Contains(diagnostics, message => message.Contains("HTTP transport failed", StringComparison.Ordinal));
    }

    private sealed class ProbeLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Events.Add((logLevel, formatter(state, exception)));
    }
}
