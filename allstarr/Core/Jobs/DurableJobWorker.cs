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
    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public DurableJobWorker(
        DurableJobQueue queue,
        DurableJobOptions options,
        DurableStorageState storageState,
        IServiceProvider services,
        IEnumerable<IDurableJobHandler> handlers,
        ILogger<DurableJobWorker> logger)
    {
        _queue = queue;
        _options = options;
        _storageState = storageState;
        _services = services;
        _logger = logger;
        _handlers = handlers.ToDictionary(
            handler => handler.JobType.Trim().ToLowerInvariant(),
            StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new List<RunningExecution>();
        var recoveryAttempted = false;
        var faultBackoffMilliseconds = Math.Max(50, _options.PollIntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ObserveCompletedAsync(active);
                if (_storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
                {
                    await Delay(stoppingToken);
                    continue;
                }

                if (!recoveryAttempted)
                {
                    await _queue.RecoverInterruptedAsync(stoppingToken);
                    recoveryAttempted = true;
                }

                await MonitorCurrentExecutionsAsync(active, stoppingToken);
                var eligibleTypes = GetEligibleTypes(active);
                if (eligibleTypes.Length == 0)
                {
                    await WaitForProgressAsync(active, stoppingToken);
                    continue;
                }

                var claim = await _queue.ClaimNextAsync(
                    _workerId,
                    eligibleTypes,
                    stoppingToken);
                if (claim == null)
                {
                    await WaitForProgressAsync(active, stoppingToken);
                    continue;
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var execution = new RunningExecution(
                    claim,
                    cancellation,
                    ExecuteClaimAsync(claim, cancellation, stoppingToken));
                active.Add(execution);
                faultBackoffMilliseconds = Math.Max(50, _options.PollIntervalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Durable job worker loop failed ({ExceptionType})",
                    exception.GetType().Name);
                try
                {
                    await Task.Delay(faultBackoffMilliseconds, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                faultBackoffMilliseconds = Math.Min(
                    faultBackoffMilliseconds * 2,
                    2000);
            }
        }

        foreach (var execution in active)
            execution.Cancellation.Cancel();
        await ObserveAllAsync(active);
    }

    private string[] GetEligibleTypes(IReadOnlyCollection<RunningExecution> active)
    {
        if (_handlers.Count == 0 || active.Count >= _options.MaximumConcurrentJobs)
        {
            return [];
        }

        var activeByType = active
            .GroupBy(item => item.Claim.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return _handlers.Keys
            .Where(type =>
            {
                activeByType.TryGetValue(type, out var count);
                return count < TypeLimit(type);
            })
            .ToArray();
    }

    private int TypeLimit(string type) =>
        _options.TypeConcurrencyLimits.TryGetValue(type, out var configured)
            ? configured
            : 1;

    private async Task MonitorCurrentExecutionsAsync(
        IReadOnlyCollection<RunningExecution> active,
        CancellationToken stoppingToken)
    {
        foreach (var execution in active)
        {
            if (execution.Task.IsCompleted)
                continue;

            try
            {
                if (!await _queue.IsExecutionCurrentAsync(execution.Claim, stoppingToken))
                    execution.Cancellation.Cancel();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ExecuteClaimAsync(
        DurableJobClaim claim,
        CancellationTokenSource executionCancellation,
        CancellationToken stoppingToken)
    {
        DurableJobCompletion? completion = null;
        try
        {
            if (!_handlers.TryGetValue(claim.Type, out var handler))
            {
                completion = DurableJobCompletion.Failure(
                    "handler_missing",
                    "No handler is registered for this job type.");
            }
            else
            {
                var authorization = await _queue.ReauthorizeAsync(
                    claim,
                    executionCancellation.Token);
                if (!authorization.Authorized)
                {
                    completion = DurableJobCompletion.Failure(
                        authorization.ErrorCode ?? "job_context_unauthorized",
                        authorization.SafeMessage ??
                        "The saved durable job context is no longer authorized.");
                }
                else
                {
                    completion = await handler.ExecuteAsync(
                        new DurableJobExecutionContext(claim, _services)
                        {
                            ReportProgressAsync = (update, cancellationToken) =>
                                _queue.ReportProgressAsync(claim, update, cancellationToken)
                        },
                        executionCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            completion = DurableJobCompletion.Cancelled();
        }
        catch (Exception exception)
        {
            var failureDetail = SafeOperationalText.Sanitize(exception.Message, 300) ??
                "No failure detail was provided.";
            _logger.LogWarning(
                "Durable job {JobId} handler failed ({FailureKind}): {FailureDetail}",
                claim.JobId,
                exception.GetType().Name,
                failureDetail);
            completion = DurableJobCompletion.Retry("handler_exception", failureDetail);
        }
        finally
        {
            executionCancellation.Cancel();
        }

        if (completion == null || stoppingToken.IsCancellationRequested)
            return;

        await CompleteWithRetryAsync(claim, completion, stoppingToken);
    }

    private async Task CompleteWithRetryAsync(
        DurableJobClaim claim,
        DurableJobCompletion completion,
        CancellationToken stoppingToken)
    {
        var retryDelayMilliseconds = Math.Max(50, _options.PollIntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queue.CompleteAsync(claim, completion, stoppingToken);
                return;
            }
            catch (DurableJobExecutionChangedException)
            {
                _logger.LogDebug(
                    "Durable job {JobId} completion was ignored after ownership changed",
                    claim.JobId);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Durable job {JobId} completion persistence failed ({ExceptionType}); retrying",
                    claim.JobId,
                    exception.GetType().Name);
                try
                {
                    await Task.Delay(retryDelayMilliseconds, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                retryDelayMilliseconds = Math.Min(retryDelayMilliseconds * 2, 2000);
            }
        }
    }

    private async Task WaitForProgressAsync(
        IReadOnlyCollection<RunningExecution> active,
        CancellationToken stoppingToken)
    {
        if (active.Count == 0)
        {
            await Delay(stoppingToken);
            return;
        }

        var completion = Task.WhenAny(active.Select(item => item.Task));
        using var wakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(_options.PollIntervalMilliseconds, wakeCancellation.Token);
        await Task.WhenAny(completion, delay);
        wakeCancellation.Cancel();
        try
        {
            await delay;
        }
        catch (OperationCanceledException)
        {
        }

        if (!stoppingToken.IsCancellationRequested && !completion.IsCompleted)
            await MonitorCurrentExecutionsAsync(active, stoppingToken);
    }

    private async Task ObserveCompletedAsync(List<RunningExecution> active)
    {
        for (var index = active.Count - 1; index >= 0; index--)
        {
            var execution = active[index];
            if (!execution.Task.IsCompleted)
                continue;

            await ObserveExecutionAsync(execution);
            active.RemoveAt(index);
        }
    }

    private async Task ObserveAllAsync(List<RunningExecution> active)
    {
        while (active.Count > 0)
            await ObserveCompletedOrWaitAsync(active);
    }

    private async Task ObserveCompletedOrWaitAsync(List<RunningExecution> active)
    {
        await ObserveCompletedAsync(active);
        if (active.Count == 0)
            return;

        await Task.WhenAny(active.Select(item => item.Task));
    }

    private async Task ObserveExecutionAsync(RunningExecution execution)
    {
        try
        {
            await execution.Task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Durable job {JobId} execution failed ({ExceptionType})",
                execution.Claim.JobId,
                exception.GetType().Name);
        }
        finally
        {
            execution.Cancellation.Dispose();
        }
    }

    private Task Delay(CancellationToken cancellationToken) =>
        Task.Delay(_options.PollIntervalMilliseconds, cancellationToken);

    private sealed record RunningExecution(
        DurableJobClaim Claim,
        CancellationTokenSource Cancellation,
        Task Task);
}
