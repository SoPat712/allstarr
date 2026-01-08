namespace octo_fiesta.Services.Validation;

/// <summary>
/// Result of a startup validation operation
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Indicates whether the validation was successful
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Short status message (e.g., "VALID", "INVALID", "TIMEOUT", "NOT CONFIGURED")
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Detailed information about the validation result
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Color to use when displaying the status in console
    /// </summary>
    public ConsoleColor StatusColor { get; set; } = ConsoleColor.White;

    /// <summary>
    /// Additional metadata about the validation
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Creates a successful validation result
    /// </summary>
    public static ValidationResult Success(string details, Dictionary<string, object>? metadata = null)
    {
        return new ValidationResult
        {
            IsValid = true,
            Status = "VALID",
            StatusColor = ConsoleColor.Green,
            Details = details,
            Metadata = metadata ?? new()
        };
    }

    /// <summary>
    /// Creates a failed validation result
    /// </summary>
    public static ValidationResult Failure(string status, string details, ConsoleColor color = ConsoleColor.Red)
    {
        return new ValidationResult
        {
            IsValid = false,
            Status = status,
            StatusColor = color,
            Details = details
        };
    }

    /// <summary>
    /// Creates a not configured validation result
    /// </summary>
    public static ValidationResult NotConfigured(string details)
    {
        return Failure("NOT CONFIGURED", details, ConsoleColor.Red);
    }
}
