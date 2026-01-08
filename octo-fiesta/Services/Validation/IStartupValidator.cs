namespace octo_fiesta.Services.Validation;

/// <summary>
/// Interface for service startup validators
/// </summary>
public interface IStartupValidator
{
    /// <summary>
    /// Gets the name of the service being validated
    /// </summary>
    string ServiceName { get; }

    /// <summary>
    /// Validates the service configuration and connectivity
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result containing status and details</returns>
    Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken);
}
