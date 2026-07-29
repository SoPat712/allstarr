using System.Text.Json;
using allstarr.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class DurableOutboxSinkTests
{
    [Fact]
    public async Task DiagnosticSink_TruthfullyAcknowledgesWithoutClaimingExternalPublication()
    {
        var logger = new CollectingLogger<DiagnosticOutboxSink>();
        var sink = new DiagnosticOutboxSink(logger);
        var messageId = Guid.CreateVersion7();
        using var payload = JsonDocument.Parse("{\"secret\":\"must-not-be-logged\"}");
        var claim = new OutboxClaim(
            messageId,
            "job.succeeded",
            payload.RootElement.Clone(),
            Guid.CreateVersion7(),
            1,
            20,
            "fixture",
            DateTimeOffset.UtcNow.AddMinutes(1));

        await sink.PublishAsync(claim, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("Acknowledged durable event", entry, StringComparison.Ordinal);
        Assert.Contains("external publication is not configured", entry, StringComparison.Ordinal);
        Assert.Contains(messageId.ToString(), entry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-be-logged", entry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticSink_HonorsCancellationBeforeAcknowledging()
    {
        var logger = new CollectingLogger<DiagnosticOutboxSink>();
        var sink = new DiagnosticOutboxSink(logger);
        using var payload = JsonDocument.Parse("{}");
        var claim = new OutboxClaim(
            Guid.CreateVersion7(),
            "job.cancelled",
            payload.RootElement.Clone(),
            null,
            1,
            20,
            "fixture",
            DateTimeOffset.UtcNow.AddMinutes(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sink.PublishAsync(claim, cancellation.Token));
        Assert.Empty(logger.Entries);
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }
}
