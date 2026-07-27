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
using allstarr.Filters;

namespace allstarr.Tests;

public sealed class HostCompositionTests
{
    [Theory]
    [InlineData("Jellyfin", typeof(JellyfinController), typeof(SubsonicController))]
    [InlineData("Subsonic", typeof(SubsonicController), typeof(JellyfinController))]
    public void SelectedBackend_RegistersOneProtocolSurfaceAndActivatesEveryController(
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

        var backendNeutralControllers = typeof(Program).Assembly.DefinedTypes
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .Where(type => type.AsType() != typeof(JellyfinController) &&
                           type.AsType() != typeof(SubsonicController) &&
                           type.AsType() != typeof(JellyfinAdminController))
            .Select(type => type.AsType())
            .ToArray();
        Assert.All(backendNeutralControllers, controllerType =>
            Assert.Contains(controllerType, controllerTypes));

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
    public void NonAdministratorSchema_ExposesOnlyReadyAccountSelfService(
        string managementMode)
    {
        using var factory = new AllstarrFactory("Jellyfin", managementMode);
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: false);

        var result = Assert.IsType<OkObjectResult>(controller.GetSchema());
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
    public void AdministratorSchema_RetainsFullManagementSurface()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: true);

        var result = Assert.IsType<OkObjectResult>(controller.GetSchema());
        var schema = Assert.IsType<AdminUiSchemaResponse>(result.Value);

        Assert.Contains(schema.Routes, route => route.Id == "settings");
        Assert.NotEmpty(schema.Providers);
        Assert.NotEmpty(schema.ProviderSupportMatrix);
        Assert.NotEmpty(schema.ConfigSections);
    }

    [Fact]
    public void AdministratorSchema_IncludesActiveExtensionCapabilities()
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

        var result = Assert.IsType<OkObjectResult>(controller.GetSchema());
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

    private static ControllerContext Context(bool administrator)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "fixture",
            UserName = "fixture",
            IsAdministrator = administrator,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        return new ControllerContext { HttpContext = httpContext };
    }

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
        private readonly string _extensionDirectory = Path.Combine(
            Path.GetTempPath(),
            "allstarr-tests",
            Guid.NewGuid().ToString("N"),
            "extensions");

        public AllstarrFactory(
            string backend,
            string providerAccountManagementMode = "Hybrid")
        {
            _backend = backend;
            _providerAccountManagementMode = providerAccountManagementMode;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Backend:Type", _backend);
            builder.UseSetting(
                "ProviderAccounts:ManagementMode",
                _providerAccountManagementMode);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backend:Type"] = _backend,
                    ["ProviderAccounts:ManagementMode"] = _providerAccountManagementMode,
                    ["SpotifyApi:Enabled"] = "false",
                    ["SpotifyImport:Enabled"] = "false",
                    ["Storage:EnforceMutationGuard"] = "false",
                    ["Extensions:Directory"] = _extensionDirectory,
                    ["Cache:GenreDirectory"] = Path.Combine(
                        Directory.GetParent(_extensionDirectory)!.FullName,
                        "genres"),
                    ["MULTI_PROVIDER_DISABLED_PROVIDERS"] = "applemusic,deezer,qobuz,squidwtf,spotify"
                });
            });
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
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
