using System.Text.Json.Serialization;

namespace allstarr.Models.Admin;

public sealed class AdminUiSchemaResponse
{
    [JsonPropertyName("routes")]
    public List<AdminUiRoute> Routes { get; set; } = [];

    [JsonPropertyName("backends")]
    public List<AdminUiBackend> Backends { get; set; } = [];

    [JsonPropertyName("activeBackend")]
    public string ActiveBackend { get; set; } = "Jellyfin";

    [JsonPropertyName("providerAccountManagementMode")]
    public string ProviderAccountManagementMode { get; set; } = "Hybrid";

    [JsonPropertyName("providers")]
    public List<AdminUiProvider> Providers { get; set; } = [];

    [JsonPropertyName("providerSupportMatrix")]
    public List<AdminUiProviderSupport> ProviderSupportMatrix { get; set; } = [];

    [JsonPropertyName("multiProviderCategories")]
    public List<string> MultiProviderCategories { get; set; } = [];

    [JsonPropertyName("priorityGroups")]
    public List<AdminUiPriorityGroup> PriorityGroups { get; set; } = [];

    [JsonPropertyName("configSections")]
    public List<AdminUiConfigSection> ConfigSections { get; set; } = [];

    [JsonPropertyName("extensionStore")]
    public AdminUiExtensionStore ExtensionStore { get; set; } = new();

    [JsonPropertyName("pluginCapabilities")]
    public List<AdminUiPluginCapability> PluginCapabilities { get; set; } = [];
}

public sealed class AdminUiProviderSupport
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("runtimeId")]
    public string? RuntimeId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("accountScope")]
    public string AccountScope { get; set; } = "none";

    [JsonPropertyName("configuration")]
    public string Configuration { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public List<AdminUiCapabilitySupport> Capabilities { get; set; } = [];
}

public sealed class AdminUiCapabilitySupport
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = "unavailable";

    [JsonPropertyName("protocolLimit")]
    public string ProtocolLimit { get; set; } = string.Empty;

    [JsonPropertyName("testCoverage")]
    public string TestCoverage { get; set; } = string.Empty;
}

public sealed class AdminUiRoute
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("zone")]
    public string Zone { get; set; } = string.Empty;
}

public sealed class AdminUiBackend
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonPropertyName("configSchema")]
    public List<AdminUiConfigField> ConfigSchema { get; set; } = [];
}

public sealed class AdminUiProvider
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("configSchema")]
    public List<AdminUiConfigField> ConfigSchema { get; set; } = [];

    [JsonPropertyName("accountSettings")]
    public List<AdminUiConfigField> AccountSettings { get; set; } = [];

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];

    [JsonPropertyName("runtimeCapabilities")]
    public List<AdminUiProviderRuntimeCapability> RuntimeCapabilities { get; set; } = [];
}

public sealed class AdminUiProviderRuntimeCapability
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("configuration")]
    public string Configuration { get; set; } = "needs_configuration";

    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("health")]
    public string Health { get; set; } = "unknown";

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("canAttempt")]
    public bool CanAttempt { get; set; }

    [JsonPropertyName("canTest")]
    public bool CanTest { get; set; }

    [JsonPropertyName("testedAt")]
    public DateTimeOffset? TestedAt { get; set; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }
}

public sealed class AdminUiPriorityGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("envKey")]
    public string EnvKey { get; set; } = string.Empty;

    [JsonPropertyName("enabledEnvKey")]
    public string? EnabledEnvKey { get; set; }

    [JsonPropertyName("providers")]
    public List<string> Providers { get; set; } = [];
}

public sealed class AdminUiConfigSection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public List<AdminUiConfigField> Fields { get; set; } = [];
}

public sealed class AdminUiConfigField
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("valuePath")]
    public string? ValuePath { get; set; }

    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];

    [JsonPropertyName("placeholder")]
    public string? Placeholder { get; set; }

    [JsonPropertyName("sensitive")]
    public bool Sensitive { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("ownership")]
    public string Ownership { get; set; } = "durable";

    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; }

    [JsonPropertyName("helpText")]
    public string? HelpText { get; set; }

    [JsonPropertyName("requiresRestart")]
    public bool RequiresRestart { get; set; }

    [JsonPropertyName("min")]
    public int? Min { get; set; }

    [JsonPropertyName("max")]
    public int? Max { get; set; }
}

public sealed class AdminUiExtensionStore
{
    [JsonPropertyName("repositories")]
    public List<string> Repositories { get; set; } = [];

    [JsonPropertyName("registryEnvKey")]
    public string RegistryEnvKey { get; set; } = "EXTENSION_REPOSITORIES";

    [JsonPropertyName("storeEndpoint")]
    public string StoreEndpoint { get; set; } = "/api/admin/extensions/store";

    [JsonPropertyName("installedEndpoint")]
    public string InstalledEndpoint { get; set; } = "/api/admin/extensions/installed";
}

public sealed class AdminUiPluginCapability
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("supported")]
    public bool Supported { get; set; }
}
