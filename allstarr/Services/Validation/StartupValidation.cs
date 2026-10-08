using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Services.Validation;

public sealed record ValidationResult(bool IsValid, string Status, string Details)
{
    public static ValidationResult Success(string details) => new(true, "VALID", details);

    public static ValidationResult Failure(string status, string details) => new(false, status, details);

    public static ValidationResult NotConfigured(string details) => Failure("NOT CONFIGURED", details);
}

public interface IStartupValidator
{
    string ServiceName { get; }

    Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken);
}

public abstract class BaseStartupValidator(HttpClient httpClient) : IStartupValidator
{
    protected readonly HttpClient _httpClient = httpClient;

    public abstract string ServiceName { get; }

    public abstract Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken);
}

public sealed class SubsonicStartupValidator : BaseStartupValidator
{
    private readonly IOptions<SubsonicSettings> _subsonicSettings;

    public override string ServiceName => "Subsonic";

    public SubsonicStartupValidator(IOptions<SubsonicSettings> subsonicSettings, HttpClient httpClient)
        : base(httpClient)
    {
        _subsonicSettings = subsonicSettings;
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var subsonicUrl = _subsonicSettings.Value.Url;

        if (string.IsNullOrWhiteSpace(subsonicUrl))
        {
            return ValidationResult.NotConfigured("Subsonic URL not configured");
        }

        try
        {
            var pingUrl = $"{subsonicUrl.TrimEnd('/')}/rest/ping.view?v=1.16.1&c=allstarr&f=json";
            using var response = await _httpClient.GetAsync(pingUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                return content.Contains("\"status\":\"ok\"") || content.Contains("status=\"ok\"")
                    ? ValidationResult.Success("Subsonic server is accessible")
                    : ValidationResult.Success("Subsonic server is reachable");
            }
            return ValidationResult.Failure(
                $"HTTP {(int)response.StatusCode}",
                "Subsonic server returned an error");
        }
        catch (TaskCanceledException)
        {
            return ValidationResult.Failure("TIMEOUT", "Could not reach server within timeout period");
        }
        catch (HttpRequestException)
        {
            return ValidationResult.Failure("UNREACHABLE", "The Subsonic server could not be reached");
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure("ERROR", $"Subsonic validation failed ({ex.GetType().Name})");
        }
    }
}

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
