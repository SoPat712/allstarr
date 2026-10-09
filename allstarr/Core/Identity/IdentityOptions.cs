namespace allstarr.Core.Identity;

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    public string BackendInstanceId { get; set; } = "primary";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BackendInstanceId) || BackendInstanceId.Length > 200)
            throw new InvalidOperationException("Identity:BackendInstanceId must contain 1 to 200 characters.");
        BackendInstanceId = BackendInstanceId.Trim();
    }
}
