namespace allstarr.Core.Settings;

public sealed record ListeningPreferences(
    string ExplicitFilter = "All",
    bool ShowExternalLabel = true,
    bool ShowExplicitLabel = true,
    string? LyricsOrder = null,
    IReadOnlyDictionary<string, string>? ProviderModes = null,
    string? PlaybackStart = "Balanced",
    string? QualityLimit = null,
    string? HeartAction = null);

/// <summary>A listener's chosen music libraries, null for all, and a revision that rises whenever the choice is saved.</summary>
public sealed record MyLibrariesSetting(IReadOnlyList<string>? Selected, long Revision);

public sealed record PersonalListeningPreferences(
    ListeningPreferences Values,
    ListeningPreferences HouseholdDefaults,
    bool UsesHouseholdDefaults,
    string Revision)
{
    public IReadOnlyDictionary<string, string>? ProviderModeOverrides { get; init; }
    public bool CleanOnlyLocked { get; init; }
    public string? HouseholdQuality { get; init; }
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
    internal const string QualityLimit = "Audio:PersonalLimit";
    internal const string HeartAction = "Library:HeartAction";
    // Personal only, and not reset with the household defaults: the music libraries a listener's songs are picked from.
    internal const string MyLibraries = "Library:MyLibraries";

    internal static readonly string[] All =
        [ExplicitFilter, ShowExternalLabel, ShowExplicitLabel, LyricsOrder, ProviderUsePolicy.SettingKey, PlaybackStart, QualityLimit, HeartAction];

    // Every key a listener may set for themselves; the library choice stays out of All so a reset to household defaults keeps it.
    internal static readonly string[] Personal = [.. All, MyLibraries];
}
