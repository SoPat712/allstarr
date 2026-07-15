using System.Diagnostics;
using allstarr.Core.Storage;
using allstarr.Core.Operations;

namespace allstarr.Core.Jobs;

public interface IOutboxSink
{
    Task PublishAsync(OutboxClaim message, CancellationToken cancellationToken);
}

/// <summary>
/// The built-in sink acknowledges an outbox record by writing redacted diagnostic metadata.
/// It is not an external event publisher. Deployments that need integration delivery must
/// replace <see cref="IOutboxSink"/> with a sink that performs and confirms that delivery.
/// </summary>
public sealed class DiagnosticOutboxSink(ILogger<DiagnosticOutboxSink> logger) : IOutboxSink
{
    public Task PublishAsync(OutboxClaim message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation(
            "Acknowledged durable event {EventType} with message {MessageId} in the diagnostic sink; external publication is not configured",
            message.Type,
            message.MessageId);
        return Task.CompletedTask;
    }
}

public sealed class DurableOutboxDispatcher : BackgroundService
{
    private readonly DurableOutbox _outbox;
    private readonly DurableJobOptions _options;
    private readonly DurableStorageState _storageState;
    private readonly IOutboxSink _sink;
    private readonly ILogger<DurableOutboxDispatcher> _logger;
    private readonly IDurableStorageRuntimeProbe _storageProbe;
    private readonly string _workerId = $"outbox:{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public DurableOutboxDispatcher(
        DurableOutbox outbox,
        DurableJobOptions options,
        DurableStorageState storageState,
        IOutboxSink sink,
        ILogger<DurableOutboxDispatcher> logger,
        IDurableStorageRuntimeProbe storageProbe)
    {
        _outbox = outbox;
        _options = options;
        _storageState = storageState;
        _sink = sink;
        _logger = logger;
        _storageProbe = storageProbe;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _storageProbe.CheckAsync(stoppingToken);

                if (_storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
                {
                    await Delay(stoppingToken);
                    continue;
                }

                var message = await _outbox.ClaimNextAsync(_workerId, stoppingToken);
                if (message == null)
                {
                    await Delay(stoppingToken);
                    continue;
                }

                using var activity = PlatformDiagnostics.ActivitySource.StartActivity("outbox.deliver");
                activity?.SetTag("outbox.message_id", message.MessageId);
                activity?.SetTag("outbox.event_type", message.Type);
                using var logScope = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["TraceId"] = activity?.TraceId.ToString() ?? "unavailable",
                    ["MessageId"] = message.MessageId,
                    ["EventType"] = message.Type
                });
                try
                {
                    await _sink.PublishAsync(message, stoppingToken);
                    await _outbox.MarkDeliveredAsync(message, stoppingToken);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    activity?.SetTag("outbox.outcome", "delivered");
                    PlatformDiagnostics.OutboxDelivered.Add(
                        1,
                        new KeyValuePair<string, object?>("event.type", message.Type));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error);
                    _logger.LogWarning(
                        "Outbox delivery {MessageId} failed ({ExceptionType})",
                        message.MessageId,
                        ex.GetType().Name);
                    var failure = await _outbox.MarkFailedAsync(
                        message,
                        "outbox_sink_failed",
                        "The event sink was unavailable for this delivery attempt.",
                        cancellationToken: stoppingToken);
                    activity?.SetTag(
                        "outbox.outcome",
                        failure.Terminal ? "terminal_failed" : "retry_scheduled");
                    if (failure.Terminal)
                    {
                        _logger.LogError(
                            "Outbox delivery {MessageId} reached its terminal attempt limit ({AttemptCount}/{MaxAttempts})",
                            message.MessageId,
                            failure.AttemptCount,
                            failure.MaxAttempts);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Durable outbox dispatcher loop failed ({ExceptionType})",
                    ex.GetType().Name);
                await Delay(stoppingToken);
            }
        }
    }

    private Task Delay(CancellationToken cancellationToken) =>
        Task.Delay(_options.PollIntervalMilliseconds, cancellationToken);
}
