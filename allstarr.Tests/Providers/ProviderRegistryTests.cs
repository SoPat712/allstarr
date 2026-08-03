using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void Registry_ValidatesThenOrdersProvidersAndCapabilityQueriesDeterministically()
    {
        var registry = new ProviderRegistry([
            Registration("z-provider"),
            Registration("a-provider"),
            Registration(
                "configured-provider",
                ProviderCapabilitySupportState.ConfiguredOnly,
                hooks: [])
        ]);

        Assert.Equal(
            ["a-provider", "configured-provider", "z-provider"],
            registry.Providers.Select(item => item.Id));
        Assert.Equal(
            ["a-provider", "z-provider"],
            registry.FindByCapability(ProviderCapabilityKind.Metadata).Select(item => item.Id));
        Assert.Equal(
            ["a-provider", "configured-provider", "z-provider"],
            registry.FindByCapability(
                ProviderCapabilityKind.Metadata,
                includeNonOperational: true).Select(item => item.Id));
    }

    [Fact]
    public void Registry_RejectsDuplicateProviderAndCapabilityDeclarations()
    {
        Assert.Throws<InvalidOperationException>(() => new ProviderRegistry([
            Registration("same-provider"),
            Registration("same-provider")
        ]));

        var duplicateCapability = BaseDescriptor(
            "duplicate-capability",
            capabilities: [Metadata(), Metadata()]);
        Assert.Throws<InvalidOperationException>(() => new ProviderRegistry([
            new ProviderRegistration(duplicateCapability, [new FakeMetadataCapability("duplicate-capability")])
        ]));
    }

    [Theory]
    [InlineData("getStreamLease")]
    [InlineData("unknownHook")]
    public void ManifestValidator_RejectsWrongOrUnknownHooks(string hook)
    {
        var descriptor = Descriptor("bad-hook", hooks: [hook]);

        Assert.Throws<InvalidOperationException>(() => ProviderManifestValidator.Validate(descriptor));
    }

    [Fact]
    public void ManifestValidator_RejectsUnsupportedSdkEntryTraversalAndHealthMismatch()
    {
        Assert.Throws<InvalidOperationException>(() => ProviderManifestValidator.Validate(
            BaseDescriptor("sdk-two", sdkVersion: "2")));
        Assert.Throws<InvalidOperationException>(() => ProviderManifestValidator.Validate(
            BaseDescriptor(
                "traversal",
                origin: ProviderOrigin.Extension,
                entryPoint: "../index.js")));
        Assert.Throws<InvalidOperationException>(() => ProviderManifestValidator.Validate(
            BaseDescriptor("health-mismatch", healthProbe: true)));
    }

    [Fact]
    public void ManifestValidator_RequiresSecretSettingsAndPermissionsToAgreeExactly()
    {
        var secret = new ProviderSettingDescriptor(
            "apiToken",
            ProviderSettingValueKind.Secret,
            ProviderSettingScope.ProviderAccount,
            "API token",
            required: true,
            helpText: "Token supplied by the provider.",
            defaultJson: "\"demo\"");
        Assert.Equal("Token supplied by the provider.", secret.HelpText);
        Assert.Equal("\"demo\"", secret.DefaultJson);
        var undeclaredPermission = BaseDescriptor(
            "secret-provider",
            settings: [secret],
            permissions: new ProviderPermissionDescriptor());
        var unknownPermission = BaseDescriptor(
            "unknown-secret",
            permissions: new ProviderPermissionDescriptor(secretSettingKeys: ["otherToken"]));

        Assert.Throws<InvalidOperationException>(() =>
            ProviderManifestValidator.Validate(undeclaredPermission));
        Assert.Throws<InvalidOperationException>(() =>
            ProviderManifestValidator.Validate(unknownPermission));

        var valid = BaseDescriptor(
            "valid-secret",
            settings: [secret],
            permissions: new ProviderPermissionDescriptor(secretSettingKeys: ["apiToken"]));
        Assert.Same(valid, ProviderManifestValidator.Validate(valid));
    }

    [Fact]
    public void ManifestMetadata_RejectsUnsafeOriginsAndLogoPaths()
    {
        Assert.Throws<ArgumentException>(() => new ProviderPermissionDescriptor(
            networkOrigins: [new Uri("https://api.example.invalid/path")]));
        Assert.Throws<ArgumentException>(() => new ProviderPermissionDescriptor(
            networkOrigins: [new Uri("http://api.example.invalid/")]));
        Assert.Throws<ArgumentException>(() => new ProviderBrandingDescriptor("../../secret.svg"));

        var permissions = new ProviderPermissionDescriptor(
            networkOrigins: [new Uri("https://api.example.invalid/")]);
        var branding = new ProviderBrandingDescriptor("images/providers/example.svg");
        Assert.Single(permissions.NetworkOrigins);
        Assert.Equal("images/providers/example.svg", branding.LogoReference);
    }

    [Fact]
    public void Registration_ProducesTheSameValidatedDeterministicRegistry()
    {
        var services = new ServiceCollection();
        services.AddProviderCapabilities();
        services.AddProviderRegistration(Registration("z-provider"));
        services.AddProviderRegistration(Registration("a-provider"));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IProviderRegistry>();

        Assert.Equal(["a-provider", "z-provider"], registry.Providers.Select(item => item.Id));
        Assert.Same(registry.GetRequired("a-provider"), registry.Providers[0]);
        Assert.IsType<FakeMetadataCapability>(registry.GetRequiredCapability<IProviderMetadataCapability>(
            "a-provider",
            ProviderCapabilityKind.Metadata));
        Assert.False(registry.TryGet("missing-provider", out _));
    }

    [Fact]
    public void Registration_RejectsMissingMismatchedAndUntypedImplementations()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProviderRegistrationValidator.Validate(new ProviderRegistration(Descriptor("missing-provider"))));
        Assert.Throws<InvalidOperationException>(() => ProviderRegistrationValidator.Validate(
            new ProviderRegistration(
                Descriptor("expected-provider"),
                [new FakeMetadataCapability("other-provider")])));
        Assert.Throws<InvalidOperationException>(() => ProviderRegistrationValidator.Validate(
            new ProviderRegistration(
                Descriptor("untyped-provider"),
                [new UntypedCapability("untyped-provider", ProviderCapabilityKind.Metadata)])));
    }

    [Fact]
    public void PlaylistManifest_RequiresExplicitProviderAccountScope()
    {
        var descriptor = BaseDescriptor(
            "playlist-provider",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Playlist,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1.0",
                    hooks: ["getUserPlaylists", "getPlaylistTracks"])
            ]);

        Assert.Throws<InvalidOperationException>(() =>
            ProviderManifestValidator.Validate(descriptor));
    }

    [Fact]
    public void ExtensionPlaylistManifest_CannotDeclareHostOnlyMutationHook()
    {
        var descriptor = BaseDescriptor(
            "playlist-extension",
            origin: ProviderOrigin.Extension,
            entryPoint: "index.js",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Playlist,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1.0",
                    hooks: ["getUserPlaylists", "getPlaylistTracks", "mutatePlaylist"],
                    allowedAccountScopes: [ProviderAccountScope.User])
            ]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProviderManifestValidator.Validate(descriptor));
        Assert.Contains("host-only", exception.Message, StringComparison.Ordinal);
    }

    private static ProviderRegistration Registration(
        string id,
        ProviderCapabilitySupportState state = ProviderCapabilitySupportState.Supported,
        IEnumerable<string>? hooks = null)
    {
        var descriptor = Descriptor(id, state, hooks);
        return new ProviderRegistration(
            descriptor,
            state is ProviderCapabilitySupportState.Supported or ProviderCapabilitySupportState.Experimental
                ? [new FakeMetadataCapability(id)]
                : []);
    }

    [Fact]
    public void DynamicExtensionRegistration_CannotReplaceBuiltInAndCanBeRolledForwardOrRemoved()
    {
        var builtIn = new ProviderRegistration(Descriptor("built-in"), [new FakeMetadataCapability("built-in")]);
        var registry = new ProviderRegistry([builtIn]);
        var first = new ProviderRegistration(
            BaseDescriptor("fixture-extension", origin: ProviderOrigin.Extension, entryPoint: "index.js"),
            [new FakeMetadataCapability("fixture-extension")]);

        registry.RegisterOrReplaceExtension(first);
        Assert.True(registry.TryGetCapability<IProviderMetadataCapability>("fixture-extension", ProviderCapabilityKind.Metadata, out _));
        registry.RegisterOrReplaceExtension(new ProviderRegistration(
            BaseDescriptor("fixture-extension", origin: ProviderOrigin.Extension, entryPoint: "index.js", sdkVersion: "1"),
            [new FakeMetadataCapability("fixture-extension")]));
        Assert.Single(registry.Providers, item => item.Id == "fixture-extension");
        Assert.True(registry.RemoveExtension("fixture-extension"));
        Assert.False(registry.TryGet("fixture-extension", out _));

        Assert.Throws<InvalidOperationException>(() => registry.RegisterOrReplaceExtension(
            new ProviderRegistration(
                BaseDescriptor("built-in", origin: ProviderOrigin.Extension, entryPoint: "index.js"),
                [new FakeMetadataCapability("built-in")])));
        Assert.False(registry.RemoveExtension("built-in"));
    }

    private static ProviderDescriptor Descriptor(
        string id,
        ProviderCapabilitySupportState state = ProviderCapabilitySupportState.Supported,
        IEnumerable<string>? hooks = null) =>
        BaseDescriptor(id, capabilities: [Metadata(state, hooks)]);

    private static ProviderCapabilityDescriptor Metadata(
        ProviderCapabilitySupportState state = ProviderCapabilitySupportState.Supported,
        IEnumerable<string>? hooks = null) => new(
        ProviderCapabilityKind.Metadata,
        state,
        ProviderAccountRequirement.None,
        compatibilityVersion: "1.0",
        hooks: hooks ?? ["searchTracks", "getTrack"]);

    private static ProviderDescriptor BaseDescriptor(
        string id,
        IEnumerable<ProviderCapabilityDescriptor>? capabilities = null,
        ProviderOrigin origin = ProviderOrigin.BuiltIn,
        string sdkVersion = "1",
        string? entryPoint = null,
        bool healthProbe = false,
        IEnumerable<ProviderSettingDescriptor>? settings = null,
        ProviderPermissionDescriptor? permissions = null) => new(
        id,
        id,
        $"{id} provider",
        origin,
        sdkVersion,
        compatibilityVersion: "1.0",
        capabilities ?? [Metadata()],
        permissions ?? new ProviderPermissionDescriptor(),
        settings,
        entryPoint: entryPoint,
        healthProbe: healthProbe);

    private sealed class FakeMetadataCapability(string providerId) : IProviderMetadataCapability
    {
        public string ProviderId { get; } = providerId;

        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

        public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
            ProviderExecutionContext context,
            ProviderMetadataSearchRequest request) => throw new NotImplementedException();

        public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
            ProviderExecutionContext context,
            ProviderTrackLookupRequest request) => throw new NotImplementedException();

        public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
            ProviderExecutionContext context,
            ProviderIsrcLookupRequest request) => throw new NotImplementedException();

        public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
            ProviderExecutionContext context,
            ProviderMetadataSearchRequest request) => throw new NotImplementedException();

        public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
            ProviderExecutionContext context,
            ProviderAlbumLookupRequest request) => throw new NotImplementedException();

        public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
            ProviderExecutionContext context,
            ProviderMetadataSearchRequest request) => throw new NotImplementedException();

        public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
            ProviderExecutionContext context,
            ProviderArtistLookupRequest request) => throw new NotImplementedException();
    }

    private sealed class UntypedCapability(
        string providerId,
        ProviderCapabilityKind capability) : IProviderCapability
    {
        public string ProviderId { get; } = providerId;

        public ProviderCapabilityKind Capability { get; } = capability;
    }
}
