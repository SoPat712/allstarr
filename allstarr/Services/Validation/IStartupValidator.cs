namespace allstarr.Services.Validation;

public interface IStartupValidator
{
    string ServiceName { get; }

    Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken);
}
