using System.ComponentModel;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2SessionResponse(
    [property: JsonPropertyName("listener")] V2Listener Listener,
    [property: JsonPropertyName("serverVersion")] string ServerVersion,
    [property: JsonPropertyName("features")] IReadOnlyDictionary<string, bool> Features,
    [property: JsonPropertyName("dashboard"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] V2Dashboard? Dashboard,
    [property: JsonPropertyName("library"), Description("The media server's own mark, shown on library results beside the services'.")] V2Provider Library,
    [property: JsonPropertyName("libraryAdditions"), Description("How this listener adds a service song or album to the library.")] V2LibraryAdditionCapabilities LibraryAdditions)
{
    /// <summary>Changes with every release, unlike serverVersion; null for development builds.</summary>
    [JsonPropertyName("build"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Build { get; init; } = AppVersion.Build;
}

public sealed record V2Listener(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record V2Dashboard([property: JsonPropertyName("url")] string Url);

public sealed record V2LibraryAdditionCapabilities(
    [property: JsonPropertyName("canAddDirectly"), Description("Adds start at once; otherwise they wait for an administrator.")] bool CanAddDirectly,
    [property: JsonPropertyName("requiresApproval")] bool RequiresApproval,
    [property: JsonPropertyName("defaultUnit"), Description("`song` or `album`: what an add button adds unless the listener picks.")] string DefaultUnit)
{
    [JsonPropertyName("setupNeeded"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     Description("Present while every add would be refused: the sentence to show, such as that a naming pattern must be confirmed in library settings. Absent when adds can start.")]
    public string? SetupNeeded { get; init; }
}
