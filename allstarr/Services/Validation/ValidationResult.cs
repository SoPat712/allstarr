namespace allstarr.Services.Validation;

public sealed record ValidationResult(bool IsValid, string Status, string Details)
{
    public static ValidationResult Success(string details) => new(true, "VALID", details);

    public static ValidationResult Failure(string status, string details) => new(false, status, details);

    public static ValidationResult NotConfigured(string details) => Failure("NOT CONFIGURED", details);
}
