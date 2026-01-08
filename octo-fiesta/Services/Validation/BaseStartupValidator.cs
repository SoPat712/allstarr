namespace octo_fiesta.Services.Validation;

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
    /// Writes a status line to the console with colored output
    /// </summary>
    protected static void WriteStatus(string label, string value, ConsoleColor valueColor)
    {
        Console.Write($"  {label}: ");
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = valueColor;
        Console.WriteLine(value);
        Console.ForegroundColor = originalColor;
    }

    /// <summary>
    /// Writes a detail line to the console in dark gray
    /// </summary>
    protected static void WriteDetail(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    -> {message}");
        Console.ForegroundColor = originalColor;
    }

    /// <summary>
    /// Masks a secret string for display, showing only the first few characters
    /// </summary>
    protected static string MaskSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "(empty)";
        }

        const int visibleChars = 4;
        if (secret.Length <= visibleChars)
        {
            return new string('*', secret.Length);
        }

        return secret[..visibleChars] + new string('*', Math.Min(secret.Length - visibleChars, 8));
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
            
            HttpRequestException httpEx => ValidationResult.Failure("UNREACHABLE", 
                httpEx.Message, ConsoleColor.Yellow),
            
            _ => ValidationResult.Failure("ERROR", ex.Message, ConsoleColor.Red)
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
