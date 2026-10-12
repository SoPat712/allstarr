using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Core.Identity;

public static class ProviderAccountNames
{
    public static string? DefaultName(string providerId, IProviderRegistry? registry = null) =>
        registry != null && registry.TryGet(providerId, out var provider) && provider != null
            ? provider.DisplayName
            : CurrentProviderSupportCatalog.All.FirstOrDefault(item =>
                item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.Name;

    public static bool IsDefault(string label, string providerId, string name)
    {
        if (label.Equals(name, StringComparison.Ordinal) ||
            label.StartsWith("Legacy .env import", StringComparison.Ordinal)) return true;
        foreach (var prefix in new[] { "My ", "Shared " })
            foreach (var source in new[] { providerId, name })
                foreach (var suffix in new[] { "", " account", " connection" })
                    if (label.Equals(prefix + source + suffix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string DisplayName(ProviderAccountRecord account, IProviderRegistry? registry = null) =>
        DefaultName(account.ProviderId, registry) is { } name && IsDefault(account.DisplayName, account.ProviderId, name)
            ? name : account.DisplayName;

    public static string? CustomLabel(ProviderAccountRecord account, IProviderRegistry? registry = null) =>
        DefaultName(account.ProviderId, registry) is { } name && IsDefault(account.DisplayName, account.ProviderId, name)
            ? null : account.DisplayName;
}
