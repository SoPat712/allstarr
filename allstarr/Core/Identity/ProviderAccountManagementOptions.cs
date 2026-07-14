namespace allstarr.Core.Identity;

public enum ProviderAccountManagementMode
{
    AdminManaged,
    UserManaged,
    Hybrid
}

public sealed class ProviderAccountManagementOptions
{
    public const string SectionName = "ProviderAccounts";

    public string ManagementMode { get; set; } = nameof(ProviderAccountManagementMode.Hybrid);

    public ProviderAccountManagementMode ParseManagementMode()
    {
        var configured = ManagementMode?.Trim();
        var supportedName = Enum.GetNames<ProviderAccountManagementMode>()
            .SingleOrDefault(name => name.Equals(configured, StringComparison.OrdinalIgnoreCase));
        if (supportedName == null)
        {
            throw new InvalidOperationException(
                "ProviderAccounts:ManagementMode must be AdminManaged, UserManaged, or Hybrid.");
        }

        return Enum.Parse<ProviderAccountManagementMode>(supportedName);
    }
}
