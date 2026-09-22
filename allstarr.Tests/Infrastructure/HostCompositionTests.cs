using allstarr.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using allstarr.Services.Validation;
using allstarr.Models.Admin;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Core.Configuration;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Filters;
using allstarr.Services.Common;
using allstarr.Services;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using System.Reflection;
using System.Collections.Immutable;
using allstarr.Core.Settings;

namespace allstarr.Tests;

public sealed class HostCompositionTests
{
    [Fact]
    public void RetiredSquidWtfProviderIsNotComposed()
    {
        using var factory = new AllstarrFactory("Jellyfin");

        Assert.DoesNotContain(factory.Services.GetRequiredService<IProviderRegistry>().Providers,
            provider => provider.Id == "squidwtf");
        Assert.DoesNotContain(factory.Services.GetServices<IConcreteMetadataService>(),
            service => service.GetType().Name.Contains("SquidWTF", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(factory.Services.GetServices<IConcreteDownloadService>(),
            service => service.GetType().Name.Contains("SquidWTF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtensionLifecycleUsesOnlyDurablePackageRoutes()
    {
        var routes = typeof(ExtensionController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), true)
                .Cast<HttpMethodAttribute>())
            .Select(attribute => attribute.Template)
            .ToArray();

        Assert.Contains("packages/{packageId:guid}/activate", routes);
        Assert.Contains("packages/{packageId:guid}/disable", routes);
        Assert.Contains("packages/{packageId:guid}", routes);
        Assert.DoesNotContain(routes, route => route is "repos" or "installed" or
            "uninstall/{id}" or "disable/{id}" or "enable/{id}");
    }

    [Theory]
    [InlineData("Jellyfin", typeof(JellyfinController), typeof(SubsonicController))]
    [InlineData("Subsonic", typeof(SubsonicController), typeof(JellyfinController))]
    public void CoreRelease_RegistersOneProtocolSurfaceAndOmitsDeferredControllers(
        string backend,
        Type expectedProtocolController,
        Type excludedProtocolController)
    {
        using var factory = new AllstarrFactory(backend);
        var descriptors = factory.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToList();
        var controllerTypes = descriptors
            .Select(item => item.ControllerTypeInfo.AsType())
            .Distinct()
            .ToList();

        Assert.Contains(expectedProtocolController, controllerTypes);
        Assert.DoesNotContain(excludedProtocolController, controllerTypes);
        Assert.Equal(
            backend.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase),
            controllerTypes.Contains(typeof(JellyfinAdminController)));
        Assert.DoesNotContain(typeof(IntelligenceController), controllerTypes);
        Assert.DoesNotContain(typeof(ListenBrainzIntakeController), controllerTypes);

        var backendNeutralControllers = typeof(Program).Assembly.DefinedTypes
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .Where(type => type.AsType() != typeof(JellyfinController) &&
                           type.AsType() != typeof(SubsonicController) &&
                           type.AsType() != typeof(JellyfinAdminController) &&
                           type.GetCustomAttribute<ReleaseFeatureAttribute>() == null)
            .Select(type => type.AsType())
            .ToArray();
        Assert.All(backendNeutralControllers, controllerType =>
            Assert.Contains(controllerType, controllerTypes));

        var handlers = factory.Services.GetServices<IDurableJobHandler>().Select(item => item.GetType()).ToArray();
        Assert.DoesNotContain(typeof(RecommendationRunJobHandler), handlers);
        Assert.DoesNotContain(typeof(GeneratedSetMaterializationJobHandler), handlers);
        Assert.DoesNotContain(typeof(ListeningHistoryImportJobHandler), handlers);
        Assert.DoesNotContain(typeof(MusicBrainzListeningEnrichmentJobHandler), handlers);
        Assert.Contains(typeof(PlaybackSignalJobHandler), handlers);
        Assert.Empty(factory.Services.GetServices<IRecommendationProvider>());

        var startupValidators = factory.Services.GetServices<IStartupValidator>().ToList();
        Assert.Single(startupValidators);
        Assert.Contains(backend, startupValidators[0].ServiceName, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        foreach (var controllerType in controllerTypes)
        {
            var exception = Record.Exception(() =>
                ActivatorUtilities.CreateInstance(scope.ServiceProvider, controllerType));
            Assert.True(exception == null, $"{backend} could not activate {controllerType.Name}: {exception}");
        }
    }

    [Fact]
    public void DevelopmentRelease_ComposesDeferredIntelligenceSurface()
    {
        using var factory = new AllstarrFactory("Jellyfin", releaseProfile: "development");
        var controllerTypes = factory.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(item => item.ControllerTypeInfo.AsType())
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IntelligenceController), controllerTypes);
        Assert.Contains(typeof(ListenBrainzIntakeController), controllerTypes);
        Assert.NotEmpty(factory.Services.GetServices<IRecommendationProvider>());
        Assert.Contains(factory.Services.GetServices<IDurableJobHandler>(),
            handler => handler is RecommendationRunJobHandler);
        Assert.Contains(factory.Services.GetServices<IDurableJobHandler>(),
            handler => handler is MusicBrainzListeningEnrichmentJobHandler);
    }

    [Theory]
    [InlineData(typeof(JellyfinController), typeof(JellyfinAuthFilter))]
    [InlineData(typeof(SubsonicController), typeof(SubsonicAuthFilter))]
    public void ProtocolControllers_CreateExecutionContextAfterAuthentication(
        Type controllerType,
        Type authenticationFilterType)
    {
        var filters = controllerType
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: true)
            .Cast<ServiceFilterAttribute>()
            .ToList();
        var authentication = Assert.Single(filters, filter =>
            filter.ServiceType == authenticationFilterType);
        var executionContext = Assert.Single(filters, filter =>
            filter.ServiceType == typeof(ProtocolExecutionContextFilter));

        Assert.True(authentication.Order < executionContext.Order);
    }

    [Fact]
    public void SubsonicController_UsesSafeExceptionFilter()
    {
        var filters = typeof(SubsonicController)
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: true)
            .Cast<ServiceFilterAttribute>();

        Assert.Single(filters, filter => filter.ServiceType == typeof(SubsonicExceptionFilter));
    }

    [Fact]
    public async Task HealthEndpoints_SeparateProcessLivenessFromDurableReadiness()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains(
            "database_unavailable",
            await ready.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("AdminManaged")]
    [InlineData("UserManaged")]
    [InlineData("Hybrid")]
    public async Task NonAdministratorSchema_ExposesOnlyReadyAccountSelfService(
        string managementMode)
    {
        using var factory = new AllstarrFactory("Jellyfin", managementMode);
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: false);

        var result = Assert.IsType<OkObjectResult>(await controller.GetSchema());
        var schema = Assert.IsType<AdminUiSchemaResponse>(result.Value);

        Assert.Equal(managementMode, schema.ProviderAccountManagementMode);
        Assert.Equal(["sources", "settings"], schema.Routes.Select(route => route.Id));
        Assert.All(schema.Providers, provider =>
        {
            Assert.Empty(provider.ConfigSchema);
            Assert.Empty(provider.RuntimeCapabilities);
        });
        Assert.Empty(schema.ProviderSupportMatrix);
        Assert.Empty(schema.ConfigSections);
        Assert.Empty(schema.ExtensionStore.Repositories);
    }

    [Fact]
    public async Task AdministratorSchema_RetainsFullManagementSurface()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: true);

        var result = Assert.IsType<OkObjectResult>(await controller.GetSchema());
        var schema = Assert.IsType<AdminUiSchemaResponse>(result.Value);

        Assert.Contains(schema.Routes, route => route.Id == "settings");
        Assert.NotEmpty(schema.Providers);
        Assert.NotEmpty(schema.ProviderSupportMatrix);
        Assert.NotEmpty(schema.ConfigSections);
        Assert.Equal("/api/admin/extensions/packages", schema.ExtensionStore.InstalledEndpoint);
    }

    [Fact]
    public async Task AdministratorSchema_UsesTheSessionTenantProviderPolicy()
    {
        var tenantId = Guid.CreateVersion7();
        var orders = ProviderOrderPolicyCatalog.Definitions.ToImmutableDictionary(
            item => item.Capability,
            item => (item.Capability == ProviderCapabilityKind.Streaming
                ? new[] { "qobuz", "deezer" }
                : item.DefaultValue.Split(',')).ToImmutableArray());
        var policy = new EffectiveProviderPolicySnapshot(
            tenantId,
            orders,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "deezer"),
            "CdLossless",
            0.07);
        using var factory = new AllstarrFactory("Jellyfin", effectivePolicy: policy);
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: true, tenantId: tenantId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetSchema());
        var schema = Assert.IsType<AdminUiSchemaResponse>(result.Value);
        var streaming = Assert.Single(schema.PriorityGroups, item => item.Id == "streaming");

        Assert.Equal(["qobuz", "deezer"], streaming.Providers.Take(2));
        Assert.Equal("disabled", Assert.Single(schema.Providers, item => item.Id == "deezer").Status);
    }

    [Fact]
    public void Provider_schema_does_not_call_missing_configuration_degraded()
    {
        ProviderRuntimeStatus Status(ProviderConfigurationState configuration) => new()
        {
            Provider = "qobuz",
            Capability = "metadata",
            IsSupported = true,
            IsEnabled = true,
            Configuration = configuration,
            Health = allstarr.Services.Common.ProviderHealthState.Degraded
        };

        Assert.Equal("needs_config", AdminUiController.AggregateProviderStatus(
            [Status(ProviderConfigurationState.NeedsConfiguration)]));
        Assert.Equal("degraded", AdminUiController.AggregateProviderStatus(
            [Status(ProviderConfigurationState.Configured)]));
    }

    [Fact]
    public async Task AdministratorSchema_IncludesActiveExtensionCapabilities()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IDynamicProviderRegistry>();
        registry.RegisterOrReplaceExtension(new ProviderRegistration(
            new ProviderDescriptor("fixture-extension", "Fixture Extension", "Fixture provider",
                ProviderOrigin.Extension, "1", "1.0",
                [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata,
                    ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Optional, "1.0",
                    ["searchTracks", "getTrack"], [ProviderAccountScope.User])],
                new ProviderPermissionDescriptor(), entryPoint: "index.js"),
            [new FixtureExtensionMetadata()]));
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: true);

        var result = Assert.IsType<OkObjectResult>(await controller.GetSchema());
        var schema = Assert.IsType<AdminUiSchemaResponse>(result.Value);
        var provider = Assert.Single(schema.Providers, item => item.Id == "fixture-extension");
        Assert.Equal("Fixture provider", provider.Description);
        Assert.Contains("metadata", provider.Categories);
    }

    [Fact]
    public void Host_RegistersDeezerAsTypedBuiltInMetadataCapability()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        var registry = factory.Services.GetRequiredService<IProviderRegistry>();

        var descriptor = registry.GetRequired("deezer");
        var capability = registry.GetRequiredCapability<IProviderMetadataCapability>(
            "deezer",
            ProviderCapabilityKind.Metadata);

        Assert.Equal(ProviderOrigin.BuiltIn, descriptor.Origin);
        Assert.Equal("deezer", capability.ProviderId);
        Assert.Equal(ProviderCapabilityKind.Metadata, capability.Capability);
    }

    [Fact]
    public void Host_ResolvesTypedCapabilitiesForEveryOperationalDescriptor()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        var registry = factory.Services.GetRequiredService<IProviderRegistry>();

        AssertOperationalTypedCapabilityContract(registry);

        const string providerId = "fixture-contract-extension";
        factory.Services.GetRequiredService<IDynamicProviderRegistry>().RegisterOrReplaceExtension(
            new ProviderRegistration(
                ExtensionContractDescriptor(providerId),
                [
                    MockCapability<IProviderMetadataCapability>(
                        providerId, ProviderCapabilityKind.Metadata),
                    MockCapability<IProviderPlaylistCapability>(
                        providerId, ProviderCapabilityKind.Playlist),
                    MockCapability<IProviderStreamingCapability>(
                        providerId, ProviderCapabilityKind.Streaming),
                    MockCapability<IProviderLyricsCapability>(
                        providerId, ProviderCapabilityKind.Lyrics)
                ]));

        Assert.Contains(registry.Providers, provider => provider.Id == providerId);
        AssertOperationalTypedCapabilityContract(registry);
    }

    [Fact]
    public void LyricsFallbackClients_HaveBoundedProviderTimeouts()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();

        Assert.All(new[] { "Odesli", "Lrclib" }, name =>
            Assert.Equal(TimeSpan.FromSeconds(5), clients.CreateClient(name).Timeout));
    }

    private static ControllerContext Context(bool administrator, Guid? tenantId = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "fixture",
            UserName = "fixture",
            IsAdministrator = administrator,
            TenantId = tenantId,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        return new ControllerContext { HttpContext = httpContext };
    }

    private static void AssertOperationalTypedCapabilityContract(IProviderRegistry registry)
    {
        foreach (var provider in registry.Providers)
        {
            foreach (var capability in provider.Capabilities.Where(item =>
                         item.HasUsableImplementation &&
                         item.Capability is ProviderCapabilityKind.Metadata or
                             ProviderCapabilityKind.Playlist or
                             ProviderCapabilityKind.Streaming or
                             ProviderCapabilityKind.Lyrics))
            {
                switch (capability.Capability)
                {
                    case ProviderCapabilityKind.Metadata:
                        AssertTypedCapability<IProviderMetadataCapability>(
                            registry, provider.Id, capability);
                        break;
                    case ProviderCapabilityKind.Playlist:
                        AssertTypedCapability<IProviderPlaylistCapability>(
                            registry, provider.Id, capability);
                        break;
                    case ProviderCapabilityKind.Streaming:
                        AssertTypedCapability<IProviderStreamingCapability>(
                            registry, provider.Id, capability);
                        break;
                    case ProviderCapabilityKind.Lyrics:
                        AssertTypedCapability<IProviderLyricsCapability>(
                            registry, provider.Id, capability);
                        break;
                }
            }
        }
    }

    private static void AssertTypedCapability<TCapability>(
        IProviderRegistry registry,
        string providerId,
        ProviderCapabilityDescriptor descriptor)
        where TCapability : class, IProviderCapability
    {
        var implementation = registry.GetRequiredCapability<TCapability>(providerId, descriptor.Capability);

        Assert.Equal(providerId, implementation.ProviderId);
        Assert.Equal(descriptor.Capability, implementation.Capability);
    }

    private static TCapability MockCapability<TCapability>(
        string providerId,
        ProviderCapabilityKind capability)
        where TCapability : class, IProviderCapability
    {
        var mock = new Mock<TCapability>(MockBehavior.Strict);
        mock.SetupGet(item => item.ProviderId).Returns(providerId);
        mock.SetupGet(item => item.Capability).Returns(capability);
        return mock.Object;
    }

    private static ProviderDescriptor ExtensionContractDescriptor(string providerId) => new(
        providerId,
        "Fixture Contract Extension",
        "Four typed capabilities for registry contract coverage.",
        ProviderOrigin.Extension,
        sdkVersion: "1",
        compatibilityVersion: "fixture-v1",
        capabilities:
        [
            new ProviderCapabilityDescriptor(
                ProviderCapabilityKind.Metadata,
                ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None,
                compatibilityVersion: "1",
                hooks: ["searchTracks", "getTrack"]),
            new ProviderCapabilityDescriptor(
                ProviderCapabilityKind.Playlist,
                ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.Required,
                compatibilityVersion: "1",
                hooks: ["getUserPlaylists", "getPlaylistTracks"],
                allowedAccountScopes: [ProviderAccountScope.User]),
            new ProviderCapabilityDescriptor(
                ProviderCapabilityKind.Streaming,
                ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None,
                compatibilityVersion: "1",
                hooks: ["getStreamLease"]),
            new ProviderCapabilityDescriptor(
                ProviderCapabilityKind.Lyrics,
                ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None,
                compatibilityVersion: "1",
                hooks: ["fetchLyrics"])
        ],
        permissions: new ProviderPermissionDescriptor(),
        entryPoint: "index.js");

    private sealed class FixtureExtensionMetadata : IProviderMetadataCapability
    {
        public string ProviderId => "fixture-extension";
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;
        public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(ProviderExecutionContext context, ProviderTrackLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(ProviderExecutionContext context, ProviderIsrcLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(ProviderExecutionContext context, ProviderAlbumLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(ProviderExecutionContext context, ProviderArtistLookupRequest request) => throw new NotSupportedException();
    }

    private sealed class AllstarrFactory : WebApplicationFactory<Program>
    {
        private readonly string _backend;
        private readonly string _providerAccountManagementMode;
        private readonly string _releaseProfile;
        private readonly EffectiveProviderPolicySnapshot? _effectivePolicy;
        private readonly string _extensionDirectory = Path.Combine(
            Path.GetTempPath(),
            "allstarr-tests",
            Guid.NewGuid().ToString("N"),
            "extensions");

        public AllstarrFactory(
            string backend,
            string providerAccountManagementMode = "Hybrid",
            string releaseProfile = "core",
            EffectiveProviderPolicySnapshot? effectivePolicy = null)
        {
            _backend = backend;
            _providerAccountManagementMode = providerAccountManagementMode;
            _releaseProfile = releaseProfile;
            _effectivePolicy = effectivePolicy;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Backend:Type", _backend);
            builder.UseSetting("Release:Profile", _releaseProfile);
            builder.UseSetting(
                "ProviderAccounts:ManagementMode",
                _providerAccountManagementMode);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backend:Type"] = _backend,
                    ["Release:Profile"] = _releaseProfile,
                    ["ProviderAccounts:ManagementMode"] = _providerAccountManagementMode,
                    ["SpotifyApi:Enabled"] = "false",
                    ["SpotifyImport:Enabled"] = "false",
                    ["Storage:EnforceMutationGuard"] = "false",
                    ["Extensions:Directory"] = _extensionDirectory,
                    ["Cache:GenreDirectory"] = Path.Combine(
                        Directory.GetParent(_extensionDirectory)!.FullName,
                        "genres"),
                    ["MULTI_PROVIDER_DISABLED_PROVIDERS"] = "applemusic,deezer,qobuz,spotify"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                if (_effectivePolicy == null) return;
                services.RemoveAll<IEffectiveProviderPolicyResolver>();
                var resolver = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
                resolver.Setup(item => item.ResolveAsync(
                        _effectivePolicy.TenantId,
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(_effectivePolicy);
                services.AddSingleton(resolver.Object);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            var root = Directory.GetParent(_extensionDirectory)?.FullName;
            if (disposing && root != null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
