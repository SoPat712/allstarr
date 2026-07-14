using allstarr.Core.Storage;

namespace allstarr.Core.Capabilities;

public enum ProviderCapabilityKind
{
    Metadata,
    Streaming,
    Download,
    Playlist,
    Lyrics,
    Health
}

public enum ProviderCapabilitySupportState
{
    Supported,
    Experimental,
    ConfiguredOnly,
    Unavailable
}

public enum ProviderAccountRequirement
{
    None,
    Optional,
    Required
}

public enum ProviderOrigin
{
    BuiltIn,
    Extension
}

public enum ProviderSettingValueKind
{
    Text,
    Secret,
    Boolean,
    Integer,
    Choice
}

public enum ProviderSettingScope
{
    ProviderAccount
}

public sealed record ProviderBrandingDescriptor
{
    public ProviderBrandingDescriptor(string logoReference, string? attribution = null)
    {
        LogoReference = ProviderContractValidation.RequiredText(
            logoReference,
            nameof(logoReference),
            500);
        Attribution = ProviderContractValidation.OptionalText(attribution, nameof(attribution), 300);

        if (Uri.TryCreate(LogoReference, UriKind.Absolute, out var uri))
        {
            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException("External provider logos must use HTTPS.", nameof(logoReference));
            }
        }
        else
        {
            var normalized = LogoReference.Replace('\\', '/');
            if (Path.IsPathRooted(normalized) ||
                normalized.Split('/').Any(segment => segment is "" or "." or "..") ||
                normalized.Contains(':'))
            {
                throw new ArgumentException(
                    "Local provider logos must use a safe relative asset path.",
                    nameof(logoReference));
            }
        }
    }

    public string LogoReference { get; }

    public string? Attribution { get; }
}

public sealed record ProviderSettingDescriptor
{
    public ProviderSettingDescriptor(
        string key,
        ProviderSettingValueKind valueKind,
        ProviderSettingScope scope,
        string label,
        bool required = false,
        IEnumerable<string>? choices = null)
    {
        if (!Enum.IsDefined(valueKind))
        {
            throw new ArgumentOutOfRangeException(nameof(valueKind));
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        var normalizedChoices = (choices ?? [])
            .Select(item => ProviderContractValidation.RequiredText(item, nameof(choices), 100))
            .ToArray();
        if (normalizedChoices.Distinct(StringComparer.Ordinal).Count() != normalizedChoices.Length)
        {
            throw new ArgumentException("Setting choices cannot contain duplicates.", nameof(choices));
        }

        if (valueKind == ProviderSettingValueKind.Choice != (normalizedChoices.Length > 0))
        {
            throw new ArgumentException(
                "Choice settings require choices and other setting types cannot declare them.",
                nameof(choices));
        }

        Key = ProviderContractValidation.SettingKey(key, nameof(key));
        ValueKind = valueKind;
        Scope = scope;
        Label = ProviderContractValidation.RequiredText(label, nameof(label), 100);
        Required = required;
        Choices = Array.AsReadOnly(normalizedChoices);
    }

    public string Key { get; }

    public ProviderSettingValueKind ValueKind { get; }

    public ProviderSettingScope Scope { get; }

    public string Label { get; }

    public bool Required { get; }

    public IReadOnlyList<string> Choices { get; }
}

public sealed record ProviderPermissionDescriptor
{
    public ProviderPermissionDescriptor(
        IEnumerable<Uri>? networkOrigins = null,
        bool cache = false,
        IEnumerable<string>? secretSettingKeys = null)
    {
        var origins = (networkOrigins ?? []).ToArray();
        if (origins.Length > 32)
        {
            throw new ArgumentException("A provider cannot request more than 32 network origins.", nameof(networkOrigins));
        }
        foreach (var origin in origins)
        {
            ArgumentNullException.ThrowIfNull(origin, nameof(networkOrigins));
            if (!origin.IsAbsoluteUri ||
                !origin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(origin.UserInfo) ||
                !origin.AbsolutePath.Equals("/", StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(origin.Query) ||
                !string.IsNullOrEmpty(origin.Fragment))
            {
                throw new ArgumentException(
                    "Network permissions must be explicit HTTPS origins without user info, paths, queries, or fragments.",
                    nameof(networkOrigins));
            }
        }

        var originKeys = origins
            .Select(item => item.GetComponents(
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped).ToLowerInvariant())
            .ToArray();
        if (originKeys.Distinct(StringComparer.Ordinal).Count() != originKeys.Length)
        {
            throw new ArgumentException("Network origins cannot contain duplicates.", nameof(networkOrigins));
        }

        var secretKeys = (secretSettingKeys ?? [])
            .Select(item => ProviderContractValidation.SettingKey(item, nameof(secretSettingKeys)))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (secretKeys.Length > 64)
        {
            throw new ArgumentException(
                "A provider cannot request more than 64 secret settings.",
                nameof(secretSettingKeys));
        }
        if (secretKeys.Distinct(StringComparer.Ordinal).Count() != secretKeys.Length)
        {
            throw new ArgumentException(
                "Secret setting permissions cannot contain duplicates.",
                nameof(secretSettingKeys));
        }

        NetworkOrigins = Array.AsReadOnly(origins);
        Cache = cache;
        SecretSettingKeys = Array.AsReadOnly(secretKeys);
    }

    public IReadOnlyList<Uri> NetworkOrigins { get; }

    public bool Cache { get; }

    public IReadOnlyList<string> SecretSettingKeys { get; }
}

public sealed record ProviderCapabilityDescriptor
{
    public ProviderCapabilityDescriptor(
        ProviderCapabilityKind capability,
        ProviderCapabilitySupportState supportState,
        ProviderAccountRequirement accountRequirement,
        string compatibilityVersion,
        IEnumerable<string>? hooks = null,
        IEnumerable<ProviderAccountScope>? allowedAccountScopes = null,
        string? sidecarDependency = null)
    {
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        if (!Enum.IsDefined(supportState))
        {
            throw new ArgumentOutOfRangeException(nameof(supportState));
        }

        if (!Enum.IsDefined(accountRequirement))
        {
            throw new ArgumentOutOfRangeException(nameof(accountRequirement));
        }

        var normalizedHooks = (hooks ?? [])
            .Select(item => ProviderContractValidation.HookName(item, nameof(hooks)))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (normalizedHooks.Distinct(StringComparer.Ordinal).Count() != normalizedHooks.Length)
        {
            throw new ArgumentException("Capability hooks cannot contain duplicates.", nameof(hooks));
        }

        var scopes = (allowedAccountScopes ?? [])
            .OrderBy(item => item)
            .ToArray();
        if (scopes.Any(item => !Enum.IsDefined(item)) || scopes.Distinct().Count() != scopes.Length)
        {
            throw new ArgumentException("Account scopes must be valid and unique.", nameof(allowedAccountScopes));
        }

        if (accountRequirement == ProviderAccountRequirement.None && scopes.Length != 0 ||
            accountRequirement != ProviderAccountRequirement.None && scopes.Length == 0)
        {
            throw new ArgumentException(
                "Account scopes must be empty when no account is used and non-empty otherwise.",
                nameof(allowedAccountScopes));
        }

        Capability = capability;
        SupportState = supportState;
        AccountRequirement = accountRequirement;
        CompatibilityVersion = ProviderContractValidation.RequiredText(
            compatibilityVersion,
            nameof(compatibilityVersion),
            50);
        Hooks = Array.AsReadOnly(normalizedHooks);
        AllowedAccountScopes = Array.AsReadOnly(scopes);
        SidecarDependency = sidecarDependency == null
            ? null
            : ProviderContractValidation.ProviderId(sidecarDependency, nameof(sidecarDependency));
    }

    public ProviderCapabilityKind Capability { get; }

    public ProviderCapabilitySupportState SupportState { get; }

    public ProviderAccountRequirement AccountRequirement { get; }

    public string CompatibilityVersion { get; }

    public IReadOnlyList<string> Hooks { get; }

    public IReadOnlyList<ProviderAccountScope> AllowedAccountScopes { get; }

    public string? SidecarDependency { get; }

    public bool HasUsableImplementation =>
        SupportState is ProviderCapabilitySupportState.Supported or
            ProviderCapabilitySupportState.Experimental;
}

public sealed record ProviderDescriptor
{
    public ProviderDescriptor(
        string id,
        string displayName,
        string description,
        ProviderOrigin origin,
        string sdkVersion,
        string compatibilityVersion,
        IEnumerable<ProviderCapabilityDescriptor> capabilities,
        ProviderPermissionDescriptor permissions,
        IEnumerable<ProviderSettingDescriptor>? settings = null,
        ProviderBrandingDescriptor? branding = null,
        string? entryPoint = null,
        bool healthProbe = false)
    {
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        ArgumentNullException.ThrowIfNull(permissions);
        Id = ProviderContractValidation.ProviderId(id, nameof(id));
        DisplayName = ProviderContractValidation.RequiredText(displayName, nameof(displayName), 100);
        Description = ProviderContractValidation.RequiredText(description, nameof(description), 500);
        Origin = origin;
        SdkVersion = ProviderContractValidation.RequiredText(sdkVersion, nameof(sdkVersion), 20);
        CompatibilityVersion = ProviderContractValidation.RequiredText(
            compatibilityVersion,
            nameof(compatibilityVersion),
            50);
        Capabilities = ProviderContractValidation.Copy(capabilities);
        Permissions = permissions;
        Settings = ProviderContractValidation.Copy(settings);
        Branding = branding;
        EntryPoint = ProviderContractValidation.OptionalText(entryPoint, nameof(entryPoint), 300);
        HealthProbe = healthProbe;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public ProviderOrigin Origin { get; }

    public string SdkVersion { get; }

    public string CompatibilityVersion { get; }

    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public ProviderPermissionDescriptor Permissions { get; }

    public IReadOnlyList<ProviderSettingDescriptor> Settings { get; }

    public ProviderBrandingDescriptor? Branding { get; }

    public string? EntryPoint { get; }

    public bool HealthProbe { get; }
}
