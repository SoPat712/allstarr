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

    /// <summary>
    /// The released commit as "git-" and its first 8 characters, or null outside released images.
    /// </summary>
    public static string? Build { get; } = Environment.GetEnvironmentVariable("ALLSTARR_REVISION") is { Length: >= 8 } revision
        ? "git-" + revision[..8] : null;
}

public static class AppIdentity
{
    public const string RepositoryUrl = "https://github.com/SoPat712/allstarr";
    public static readonly string UserAgent = $"Allstarr/{AppVersion.Version} (+{RepositoryUrl})";
}
