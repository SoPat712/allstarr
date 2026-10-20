using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;

namespace allstarr.Core.Settings;

public static class ProviderUsePolicy
{
    public const string SettingKey = "Providers:UseModes";

    public static string Name(ProviderUseMode mode) => mode switch
    {
        ProviderUseMode.Automatic => "automatic",
        ProviderUseMode.ManualOnly => "manualOnly",
        ProviderUseMode.Off => "off",
        _ => throw new ArgumentException("The source use mode is invalid.")
    };

    public static ProviderUseMode ParseMode(string? value) => value switch
    {
        "automatic" => ProviderUseMode.Automatic,
        "manualOnly" => ProviderUseMode.ManualOnly,
        "off" => ProviderUseMode.Off,
        _ => throw new ArgumentException("Use automatic, manualOnly, or off.")
    };

    public static ImmutableDictionary<string, ProviderUseMode> ParseModes(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Source use modes must be an object.");
            var modes = ImmutableDictionary.CreateBuilder<string, ProviderUseMode>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var id = AppleMusicClient.NormalizeProviderId(
                    ProviderContractValidation.ProviderId(entry.Name, nameof(json)));
                if (modes.Count >= 128 || entry.Value.ValueKind != JsonValueKind.String ||
                    !modes.TryAdd(id, ParseMode(entry.Value.GetString())))
                    throw new ArgumentException("Source use modes must be valid and unique.");
            }
            return modes.ToImmutable();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Source use modes must be valid JSON.", nameof(json), exception);
        }
    }

    public static string SerializeModes(IEnumerable<KeyValuePair<string, ProviderUseMode>> modes) =>
        JsonSerializer.Serialize(modes.OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => Name(item.Value), StringComparer.Ordinal));

    public static ProviderUseMode DefaultMode(ProviderDescriptor provider) =>
        Restrict(provider, provider.DefaultUseMode);

    public static ProviderUseMode Restrict(ProviderDescriptor provider, ProviderUseMode mode) =>
        provider.MatchingMode == ProviderMatchingMode.ManualOnly && mode == ProviderUseMode.Automatic
            ? ProviderUseMode.ManualOnly : mode;
}
