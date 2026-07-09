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

    [JsonPropertyName("providers")]
    public List<AdminUiProvider> Providers { get; set; } = [];

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

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("configSchema")]
    public List<AdminUiConfigField> ConfigSchema { get; set; } = [];

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];
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

    [JsonPropertyName("requiresRestart")]
    public bool RequiresRestart { get; set; } = true;

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
