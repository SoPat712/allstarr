namespace allstarr.Core.Settings;

public sealed record ListeningPreferences(
    string ExplicitFilter = "All",
    bool ShowExternalLabel = true,
    bool ShowExplicitLabel = true);

public sealed record PersonalListeningPreferences(
    ListeningPreferences Values,
    ListeningPreferences HouseholdDefaults,
    bool UsesHouseholdDefaults,
    string Revision);

internal static class ListeningPreferenceKeys
{
    internal const string ExplicitFilter = "Library:ExplicitFilter";
    internal const string ShowExternalLabel = "Playback:ShowExternalLabel";
    internal const string ShowExplicitLabel = "Playback:ShowExplicitLabel";

    internal static readonly string[] All =
        [ExplicitFilter, ShowExternalLabel, ShowExplicitLabel];
}
