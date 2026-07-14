namespace allstarr.Core.Identity;

public enum MultiUserMode
{
    SingleUser,
    Hybrid,
    Strict
}

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    public string Mode { get; set; } = nameof(MultiUserMode.Hybrid);
    public string DefaultTenantId { get; set; } = "018f1f6e-7db7-7ab0-8b32-f26f12ff6d6a";
    public string DefaultTenantSlug { get; set; } = "default";
    public string DefaultTenantName { get; set; } = "Allstarr";
    public string SingleUserId { get; set; } = "018f1f6e-8e9c-77f5-9a79-3d8a494d60cd";
    public string BackendInstanceId { get; set; } = "primary";

    public MultiUserMode ParseMode()
    {
        if (!Enum.TryParse<MultiUserMode>(Mode, ignoreCase: true, out var parsed))
        {
            throw new InvalidOperationException("Identity:Mode must be SingleUser, Hybrid, or Strict.");
        }

        _ = GetDefaultTenantId();
        _ = GetSingleUserId();
        if (string.IsNullOrWhiteSpace(DefaultTenantSlug) || string.IsNullOrWhiteSpace(DefaultTenantName))
        {
            throw new InvalidOperationException("Identity default tenant slug and name are required.");
        }

        if (string.IsNullOrWhiteSpace(BackendInstanceId))
        {
            throw new InvalidOperationException("Identity:BackendInstanceId is required.");
        }

        return parsed;
    }

    public Guid GetDefaultTenantId() => ParseGuid(DefaultTenantId, "Identity:DefaultTenantId");
    public Guid GetSingleUserId() => ParseGuid(SingleUserId, "Identity:SingleUserId");

    private static Guid ParseGuid(string value, string key) => Guid.TryParse(value, out var parsed)
        ? parsed
        : throw new InvalidOperationException($"{key} must be a GUID.");
}
