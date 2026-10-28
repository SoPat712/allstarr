namespace allstarr.Core.Settings;

public sealed record ListeningPreferences(
    string ExplicitFilter = "All",
    bool ShowExternalLabel = true,
    bool ShowExplicitLabel = true,
    string? LyricsOrder = null,
    IReadOnlyDictionary<string, string>? ProviderModes = null,
    string? PlaybackStart = "Balanced");

public sealed record PersonalListeningPreferences(
    ListeningPreferences Values,
    ListeningPreferences HouseholdDefaults,
    bool UsesHouseholdDefaults,
    string Revision)
{
    public IReadOnlyDictionary<string, string>? ProviderModeOverrides { get; init; }
    public bool CleanOnlyLocked { get; init; }
}

internal static class CleanOnlyLockKeys
{
    private const string Prefix = "Listening:CleanOnlyLock:";

    internal static string ForUser(Guid userId) => Prefix + userId.ToString("N");

    internal static bool IsLockKey(string key) => key.StartsWith(Prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(key[Prefix.Length..], "N", out var userId) && userId != Guid.Empty;
}

internal static class ListeningPreferenceKeys
{
    internal const string ExplicitFilter = "Library:ExplicitFilter";
    internal const string ShowExternalLabel = "Playback:ShowExternalLabel";
    internal const string ShowExplicitLabel = "Playback:ShowExplicitLabel";
    internal const string LyricsOrder = "Providers:LyricsOrder";
    internal const string PlaybackStart = "Playback:StartPreference";

    internal static readonly string[] All =
        [ExplicitFilter, ShowExternalLabel, ShowExplicitLabel, LyricsOrder, ProviderUsePolicy.SettingKey, PlaybackStart];
}
