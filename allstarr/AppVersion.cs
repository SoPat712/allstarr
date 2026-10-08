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

public static class AppIdentity
{
    public const string RepositoryUrl = "https://github.com/SoPat712/allstarr";
    public static readonly string UserAgent = $"Allstarr/{AppVersion.Version} (+{RepositoryUrl})";
}
