namespace allstarr.Services.Validation;

public abstract class BaseStartupValidator(HttpClient httpClient) : IStartupValidator
{
    protected readonly HttpClient _httpClient = httpClient;

    public abstract string ServiceName { get; }

    public abstract Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken);
}
