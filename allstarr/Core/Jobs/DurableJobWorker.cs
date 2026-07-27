using System.Diagnostics;
using allstarr.Core.Operations;
using allstarr.Core.Storage;

namespace allstarr.Core.Jobs;

public sealed record DurableJobExecutionContext(
    DurableJobClaim Claim,
    IServiceProvider Services)
{
    public Func<DurableJobProgressUpdate, CancellationToken, Task<bool>> ReportProgressAsync { get; init; } =
        static (_, _) => Task.FromResult(false);
}

public interface IDurableJobHandler
{
    string JobType { get; }

    Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class DurableJobWorker : BackgroundService
{
    private readonly DurableJobQueue _queue;
    private readonly DurableJobOptions _options;
    private readonly DurableStorageState _storageState;
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<string, IDurableJobHandler> _handlers;
    private readonly ILogger<DurableJobWorker> _logger;
    private readonly IDurableStorageRuntimeProbe _storageProbe;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public DurableJobWorker(
        DurableJobQueue queue,
        DurableJobOptions options,
        DurableStorageState storageState,
        IServiceProvider services,
        IEnumerable<IDurableJobHandler> handlers,
        ILogger<DurableJobWorker> logger,
        IDurableStorageRuntimeProbe storageProbe)
    {
        _queue = queue;
        _options = options;
        _storageState = storageState;
        _services = services;
        _logger = logger;
        _storageProbe = storageProbe;
        _handlers = handlers.ToDictionary(
            handler => handler.JobType.Trim().ToLowerInvariant(),
            StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _storageProbe.CheckAsync(stoppingToken);

                if (_handlers.Count == 0 ||
                    _storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
                {
                    await Delay(stoppingToken);
                    continue;
                }

                var claim = await _queue.ClaimNextAsync(
                    _workerId,
                    _handlers.Keys.ToArray(),
                    stoppingToken);
                if (claim == null)
                {
                    await Delay(stoppingToken);
                    continue;
                }

                await ExecuteClaimAsync(claim, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Durable job worker loop failed ({ExceptionType})",
                    ex.GetType().Name);
                await Delay(stoppingToken);
            }
        }
    }

    private async Task ExecuteClaimAsync(DurableJobClaim claim, CancellationToken stoppingToken)
    {
        using var activity = PlatformDiagnostics.ActivitySource.StartActivity("durable-job.execute");
        activity?.SetTag("job.id", claim.JobId);
        activity?.SetTag("job.type", claim.Type);
        activity?.SetTag("correlation.id", claim.CorrelationId);
        activity?.SetTag("provider.capability", claim.ProviderCapability);
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = claim.CorrelationId,
            ["TraceId"] = activity?.TraceId.ToString() ?? "unavailable",
            ["JobId"] = claim.JobId,
            ["JobType"] = claim.Type
        });
        if (!_handlers.TryGetValue(claim.Type, out var handler))
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("job.outcome", "handler_missing");
            await _queue.CompleteAsync(
                claim,
                DurableJobCompletion.Failure("handler_missing", "No handler is registered for this job type."),
                stoppingToken);
            return;
        }

        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseUntilCancelled(claim, leaseCancellation, stoppingToken);
        DurableJobCompletion completion;
        try
        {
            var authorization = await _queue.ReauthorizeAsync(claim, leaseCancellation.Token);
            if (!authorization.Authorized)
            {
                PlatformDiagnostics.JobContextDenied.Add(
                    1,
                    new KeyValuePair<string, object?>("job.type", claim.Type),
                    new KeyValuePair<string, object?>(
                        "error.code",
                        authorization.ErrorCode ?? "job_context_unauthorized"));
                completion = DurableJobCompletion.Failure(
                    authorization.ErrorCode ?? "job_context_unauthorized",
                    authorization.SafeMessage ?? "The saved durable job context is no longer authorized.");
            }
            else
            {
                completion = await handler.ExecuteAsync(
                    new DurableJobExecutionContext(claim, _services)
                    {
                        ReportProgressAsync = (update, cancellationToken) =>
                            _queue.ReportProgressAsync(claim, update, cancellationToken)
                    },
                    leaseCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            leaseCancellation.Cancel();
            await IgnoreCancellation(renewal);
            return;
        }
        catch (OperationCanceledException)
        {
            completion = DurableJobCompletion.Cancelled();
        }
        catch (Exception ex)
        {
            var failureDetail = SafeOperationalText.Sanitize(ex.Message, 300) ?? "No failure detail was provided.";
            _logger.LogWarning(
                "Durable job {JobId} handler failed ({FailureKind}): {FailureDetail}",
                claim.JobId,
                ex.GetType().Name,
                failureDetail);
            completion = DurableJobCompletion.Retry(
                "handler_exception",
                failureDetail);
        }
        finally
        {
            leaseCancellation.Cancel();
        }

        await IgnoreCancellation(renewal);
        activity?.SetTag("job.outcome", completion.Kind.ToString().ToLowerInvariant());
        if (completion.Kind is DurableJobCompletionKind.Failed or DurableJobCompletionKind.Retry)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        try
        {
            await _queue.CompleteAsync(claim, completion, stoppingToken);
        }
        catch (InvalidOperationException)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("job.outcome", "lease_lost");
            _logger.LogWarning("Durable job {JobId} completion was ignored after lease loss", claim.JobId);
        }
    }

    private async Task RenewLeaseUntilCancelled(
        DurableJobClaim claim,
        CancellationTokenSource executionCancellation,
        CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.LeaseSeconds / 3));
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(executionCancellation.Token))
            {
                var renewed = await _queue.RenewLeaseAsync(claim, stoppingToken);
                if (!renewed)
                {
                    executionCancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
        }
    }

    private Task Delay(CancellationToken cancellationToken) =>
        Task.Delay(_options.PollIntervalMilliseconds, cancellationToken);

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
