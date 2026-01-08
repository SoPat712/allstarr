using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Validation;

/// <summary>
/// Orchestrates startup validation for all configured services.
/// This replaces the old StartupValidationService with a more extensible architecture.
/// </summary>
public class StartupValidationOrchestrator : IHostedService
{
    private readonly IEnumerable<IStartupValidator> _validators;
    private readonly IOptions<SubsonicSettings> _subsonicSettings;

    public StartupValidationOrchestrator(
        IEnumerable<IStartupValidator> validators,
        IOptions<SubsonicSettings> subsonicSettings)
    {
        _validators = validators;
        _subsonicSettings = subsonicSettings;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("       octo-fiesta starting up...       ");
        Console.WriteLine("========================================");
        Console.WriteLine();

        // Run all validators
        foreach (var validator in _validators)
        {
            try
            {
                await validator.ValidateAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error validating {validator.ServiceName}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("       Startup validation complete      ");
        Console.WriteLine("========================================");
        Console.WriteLine();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
