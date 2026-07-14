namespace allstarr.Services.Validation;

/// <summary>
/// Orchestrates startup validation for all configured services.
/// This replaces the old StartupValidationService with a more extensible architecture.
/// </summary>
public class StartupValidationOrchestrator : IHostedService
{
    private readonly IEnumerable<IStartupValidator> _validators;
    private readonly ILogger<StartupValidationOrchestrator> _logger;

    public StartupValidationOrchestrator(
        IEnumerable<IStartupValidator> validators,
        ILogger<StartupValidationOrchestrator> logger)
    {
        _validators = validators;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Get version from assembly
        var version = typeof(StartupValidationOrchestrator).Assembly
            .GetName().Version?.ToString(3) ?? "unknown";
        
        _logger.LogInformation("Starting provider validation for Allstarr {Version}", version);

        // Run all validators
        foreach (var validator in _validators)
        {
            try
            {
                var result = await validator.ValidateAsync(cancellationToken);
                _logger.LogInformation(
                    "Startup validation for {ServiceName} completed with {ValidationStatus}",
                    validator.ServiceName,
                    result.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Startup validation failed for {ServiceName} ({ExceptionType})",
                    validator.ServiceName,
                    ex.GetType().Name);
            }
        }
        _logger.LogInformation("Provider startup validation complete");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
