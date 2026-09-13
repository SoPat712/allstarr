namespace allstarr.Services.Validation;

public sealed class StartupValidationOrchestrator(
    IEnumerable<IStartupValidator> validators,
    ILogger<StartupValidationOrchestrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var version = typeof(StartupValidationOrchestrator).Assembly
            .GetName().Version?.ToString(3) ?? "unknown";

        logger.LogInformation("Starting provider validation for Allstarr {Version}", version);

        foreach (var validator in validators)
        {
            try
            {
                var result = await validator.ValidateAsync(cancellationToken);
                logger.LogInformation(
                    "Startup validation for {ServiceName} completed with {ValidationStatus}",
                    validator.ServiceName,
                    result.Status);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Startup validation failed for {ServiceName} ({ExceptionType})",
                    validator.ServiceName,
                    ex.GetType().Name);
            }
        }
        logger.LogInformation("Provider startup validation complete");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
