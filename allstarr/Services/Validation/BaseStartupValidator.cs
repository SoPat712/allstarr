namespace allstarr.Services.Validation;

/// <summary>
/// Base class for startup validators providing common functionality
/// </summary>
public abstract class BaseStartupValidator : IStartupValidator
{
    protected readonly HttpClient _httpClient;

    protected BaseStartupValidator(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Gets the name of the service being validated
    /// </summary>
    public abstract string ServiceName { get; }

    /// <summary>
    /// Validates the service configuration and connectivity
    /// </summary>
    public abstract Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Retained for validator compatibility. The orchestrator owns structured output.
    /// </summary>
    protected static void WriteStatus(string label, string value, ConsoleColor valueColor)
    {
        _ = label;
        _ = value;
        _ = valueColor;
    }

    /// <summary>
    /// Retained for validator compatibility. Details are returned, not printed directly.
    /// </summary>
    protected static void WriteDetail(string message)
    {
        _ = message;
    }

    /// <summary>
    /// Reports configuration without revealing any secret characters.
    /// </summary>
    protected static string MaskSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "(empty)";
        }

        return "<configured>";
    }

    /// <summary>
    /// Handles common HTTP exceptions and returns appropriate validation result
    /// </summary>
    protected static ValidationResult HandleException(Exception ex, string fieldName)
    {
        return ex switch
        {
            TaskCanceledException => ValidationResult.Failure("TIMEOUT",
                "Could not reach service within timeout period", ConsoleColor.Yellow),

            HttpRequestException => ValidationResult.Failure("UNREACHABLE",
                "The service could not be reached", ConsoleColor.Yellow),

            _ => ValidationResult.Failure(
                "ERROR",
                $"Validation failed ({ex.GetType().Name})",
                ConsoleColor.Red)
        };
    }

    /// <summary>
    /// Writes validation result to console
    /// </summary>
    protected void WriteValidationResult(string fieldName, ValidationResult result)
    {
        WriteStatus(fieldName, result.Status, result.StatusColor);
        if (!string.IsNullOrEmpty(result.Details))
        {
            WriteDetail(result.Details);
        }
    }
}
