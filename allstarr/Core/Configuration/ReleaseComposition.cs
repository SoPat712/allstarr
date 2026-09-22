namespace allstarr.Core.Configuration;

public enum ReleaseFeatureKind
{
    Intelligence
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReleaseFeatureAttribute(ReleaseFeatureKind feature) : Attribute
{
    public ReleaseFeatureKind Feature { get; } = feature;
}

public sealed record ReleaseComposition(string Profile)
{
    public const string CoreProfile = "core";
    public const string DevelopmentProfile = "development";

    public static ReleaseComposition Core { get; } = new(CoreProfile);
    public static ReleaseComposition Development { get; } = new(DevelopmentProfile);

    public bool IntelligenceEnabled => Profile == DevelopmentProfile;

    public bool Includes(ReleaseFeatureKind feature) => feature switch
    {
        ReleaseFeatureKind.Intelligence => IntelligenceEnabled,
        _ => false
    };

    public bool IncludesScheduledJob(string jobType) =>
        IntelligenceEnabled ||
        !jobType.Equals("recommendation.generate", StringComparison.OrdinalIgnoreCase);

    public static ReleaseComposition Resolve(IConfiguration configuration)
    {
        var profile = configuration["Release:Profile"]?.Trim().ToLowerInvariant();
        return profile switch
        {
            null or "" or CoreProfile => Core,
            DevelopmentProfile => Development,
            _ => throw new InvalidOperationException(
                $"Release:Profile must be '{CoreProfile}' or '{DevelopmentProfile}'.")
        };
    }
}
