namespace allstarr;

/// <summary>
/// Single source of truth for application version.
/// Update this value when releasing a new version.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Current application version.
    /// </summary>
    public const string Version = "3.1.0-beta.1";
}

/// <summary>
/// Compatibility retained for v3.0. Removal requires an explicit release decision no earlier than v3.1.
/// </summary>
public static class CompatibilitySunsets
{
    public const string RetainedThroughVersion = "3.0";
    public const string EarliestRemovalVersion = "3.1";

    public static bool LegacyEnvV2ImporterEnabled => true;
    public static bool LegacyAdminCookieEnabled => true;
    public static bool SpotiFlacTranslatorEnabled => true;
}
