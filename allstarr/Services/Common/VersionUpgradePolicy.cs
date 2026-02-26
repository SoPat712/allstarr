namespace allstarr.Services.Common;

/// <summary>
/// Defines when a version change should trigger a full playlist rebuild.
/// </summary>
public static class VersionUpgradePolicy
{
    /// <summary>
    /// Returns true when the current version is a major or minor upgrade over the previous version.
    /// Patch-only upgrades and downgrades return false.
    /// </summary>
    public static bool ShouldTriggerRebuild(string previousVersion, string currentVersion, out string reason)
    {
        reason = "no rebuild required";

        if (!Version.TryParse(previousVersion, out var previous))
        {
            reason = "previous version is invalid";
            return false;
        }

        if (!Version.TryParse(currentVersion, out var current))
        {
            reason = "current version is invalid";
            return false;
        }

        if (current.CompareTo(previous) <= 0)
        {
            reason = "version is not an upgrade";
            return false;
        }

        if (current.Major > previous.Major)
        {
            reason = "major version upgrade";
            return true;
        }

        if (current.Minor > previous.Minor)
        {
            reason = "minor version upgrade";
            return true;
        }

        reason = "patch-only upgrade";
        return false;
    }
}
