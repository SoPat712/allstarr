using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Core.Providers;

/// <summary>
/// Describes legacy built-ins that have not crossed the typed capability boundary yet.
/// ConfiguredOnly keeps them visible to the core without making them routable.
/// </summary>
public static class BuiltInProviderDescriptorCatalog
{
    private static readonly ProviderAccountScope[] MixedAccountScopes =
    [
        ProviderAccountScope.Global,
        ProviderAccountScope.User,
        ProviderAccountScope.Library
    ];

    public static IReadOnlyList<ProviderRegistration> LegacyRegistrations { get; } =
    [
        Registration(
            "musicbrainz",
            "MusicBrainz",
            "The current identity and enrichment helper before its typed metadata adapter.",
            NoAccount(ProviderCapabilityKind.Metadata)),
        Registration(
            "lastfm",
            "Last.fm",
            "The existing scrobbling integration represented by its future typed health lane.",
            Required(ProviderCapabilityKind.Health)),
        Registration(
            "listenbrainz",
            "ListenBrainz",
            "The existing scrobbling integration represented by its future typed health lane.",
            Required(ProviderCapabilityKind.Health))
    ];

    public static IServiceCollection AddLegacyBuiltInProviderDescriptors(
        this IServiceCollection services)
    {
        foreach (var registration in LegacyRegistrations)
        {
            ProviderRegistrationValidator.Validate(registration);
            services.AddSingleton(registration);
        }

        return services;
    }

    private static ProviderRegistration Registration(
        string id,
        string name,
        string description,
        params ProviderCapabilityDescriptor[] capabilities) => new(
        new ProviderDescriptor(
            id,
            name,
            description,
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "legacy-seam-v1",
            capabilities,
            new ProviderPermissionDescriptor()),
        implementations: []);

    private static ProviderCapabilityDescriptor NoAccount(
        ProviderCapabilityKind capability,
        ProviderCapabilitySupportState state = ProviderCapabilitySupportState.ConfiguredOnly) => new(
        capability,
        state,
        ProviderAccountRequirement.None,
        compatibilityVersion: "legacy-seam-v1");

    private static ProviderCapabilityDescriptor Required(
        ProviderCapabilityKind capability,
        IEnumerable<ProviderAccountScope>? scopes = null,
        ProviderCapabilitySupportState state = ProviderCapabilitySupportState.ConfiguredOnly) => new(
        capability,
        state,
        ProviderAccountRequirement.Required,
        compatibilityVersion: "legacy-seam-v1",
        allowedAccountScopes: scopes ?? MixedAccountScopes);
}
