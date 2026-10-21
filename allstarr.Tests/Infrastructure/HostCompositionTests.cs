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
    public async Task FreshPersistentHost_ServesHealthAndGuidedSetupBeforeABackendIsChosen()
    {
        using var factory = new AllstarrFactory("Jellyfin", pendingBackend: true);
        Assert.Equal(DurableStorageReadiness.Ready, factory.Services.GetRequiredService<DurableStorageState>().GetSnapshot().Readiness);
        Assert.False(factory.Services.GetRequiredService<BackendSetupService>().IsConfigured);
        var health = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
        var context = await factory.Server.SendAsync(context =>
        {
            context.Request.Path = "/api/admin/auth/setup";
            context.Request.Method = "GET";
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost", 5275);
            context.Connection.LocalPort = 5275;
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        });
        Assert.Equal(200, context.Response.StatusCode);
        using var document = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        Assert.False(document.RootElement.GetProperty("configured").GetBoolean());
        Assert.True(document.RootElement.GetProperty("storageReady").GetBoolean());
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("csrfToken").GetString()));
    }

    [Fact]
    public void BackendSetupTransport_DoesNotFollowRedirectsOrShareCookies()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(BackendConnectionVerifier.HttpClientName);
        while (handler is DelegatingHandler outer) handler = outer.InnerHandler!;
        var transport = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(BackendConnectionVerifier.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        Assert.Equal(1024 * 1024, client.MaxResponseContentBufferSize);
    }

    [Fact]
    public void RetiredSquidWtfProviderIsNotComposed()
    {
        using var factory = new AllstarrFactory("Jellyfin");

        Assert.DoesNotContain(factory.Services.GetRequiredService<IProviderRegistry>().Providers,
            provider => provider.Id == "squidwtf");
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
        Assert.DoesNotContain(typeof(IntelligenceController), controllerTypes);
        Assert.DoesNotContain(typeof(ListenBrainzIntakeController), controllerTypes);

        var routes = descriptors.Select(item => item.AttributeRouteInfo?.Template).OfType<string>().ToArray();
        Assert.Contains("api/admin/config", routes);
        Assert.Contains("api/admin/config/migration/preview", routes);
        Assert.Contains("api/admin/media-probe", routes);
        Assert.Contains("api/admin/playlist-readiness", routes);
        Assert.Contains("api/admin/ui/now-playing", routes);
        Assert.Contains("api/admin/downloads/artwork/{itemId}", routes);
        Assert.DoesNotContain(routes, route => route is
            "api/admin/status" or "api/admin/sessions" or "api/admin/scrobbling-sessions" or
            "api/admin/debug/endpoint-usage" or "api/admin/restart" or "api/admin/export-env" or
            "api/admin/import-env" or "api/admin/config/effective-provider-policy" or "api/admin/downloads/queue");

        var backendNeutralControllers = typeof(Program).Assembly.DefinedTypes
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .Where(type => type.AsType() != typeof(JellyfinController) &&
                           type.AsType() != typeof(SubsonicController) &&
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
        factory.Services.GetRequiredService<allstarr.Core.Storage.DurableStorageState>().Set(
            allstarr.Core.Storage.DurableStorageReadiness.Unavailable,
            errorCode: "database_unavailable");

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonAdministratorSchema_ExposesOnlyReadyAccountSelfService(
        bool allowConnections)
    {
        using var factory = new AllstarrFactory("Jellyfin", allowConnections);
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: false);

        var result = Assert.IsType<OkObjectResult>(await controller.GetSchema());
        var schema = Assert.IsType<AdminUiSchemaResponse>(result.Value);

        Assert.Equal(allowConnections, schema.ListenersCanConnectOwnAccounts);
        Assert.Equal(["home", "playlists", "mappings", "sources", "activity", "preferences"], schema.Routes.Select(route => route.Id));
        Assert.All(schema.Providers, provider =>
        {
            Assert.Empty(provider.ConfigSchema);
            Assert.Empty(provider.RuntimeCapabilities);
        });
        Assert.Empty(schema.ProviderSupportMatrix);
        Assert.Empty(schema.ConfigSections);
        Assert.Empty(schema.ExtensionStore.Repositories);
    }

    [Theory]
    [InlineData(false, ProviderUseMode.ManualOnly)]
    [InlineData(true, ProviderUseMode.Off)]
    public async Task SchemaSourceModeReflectsTheViewer(bool administrator, ProviderUseMode mode)
    {
        using var factory = new AllstarrFactory("Jellyfin");
        using var scope = factory.Services.CreateScope();
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        var user = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "High", 0.07)
            { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("artistgrid", mode) });
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider, policies.Object);
        controller.ControllerContext = Context(administrator);
        var schema = Assert.IsType<AdminUiSchemaResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetSchema()).Value);
        Assert.Equal(ProviderUsePolicy.Name(mode), Assert.Single(schema.Providers, item => item.Id == "artistgrid").UseMode);
        policies.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountProviders_PublishTheirProperNamesInEveryUiSchema(bool administrator)
    {
        using var factory = new AllstarrFactory("Jellyfin");
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator);
        var schema = Assert.IsType<AdminUiSchemaResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetSchema()).Value);
        foreach (var id in new[] { "lastfm", "listenbrainz", "spotify", "deezer", "apple-musickit" })
        {
            var expected = Assert.Single(CurrentProviderSupportCatalog.All, item => item.Id == id);
            Assert.Equal(expected.Name, Assert.Single(schema.Providers, item => item.Id == id).Name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LyricsSourcesExposeTheirExistingSetupAndRoutingSurface(bool disabled)
    {
        var policy = new EffectiveProviderPolicySnapshot(
            ProviderOrderPolicyCatalog.Definitions.ToImmutableDictionary(
                item => item.Capability, item => item.DefaultValue.Split(',').ToImmutableArray()),
            disabled ? ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "musixmatch") : ImmutableHashSet<string>.Empty,
            "CdLossless", 0.07);
        using var factory = new AllstarrFactory("Jellyfin", effectivePolicy: policy);
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: true);
        var schema = Assert.IsType<AdminUiSchemaResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetSchema()).Value);
        var plus = Assert.Single(schema.Providers, item => item.Id == "lyricsplus");
        Assert.Equal("operator_managed", plus.ConnectionKind);
        Assert.Equal("lyricsPlus.baseUrl", Assert.Single(plus.ConfigSchema).ValuePath);
        var musixmatch = Assert.Single(schema.Providers, item => item.Id == "musixmatch");
        Assert.Equal(disabled ? "disabled" : "available", musixmatch.Status);
        Assert.Equal("needs_config", Assert.Single(schema.Providers, item => item.Id == "qobuz").Status);
        var secret = Assert.Single(musixmatch.AccountSettings);
        Assert.Equal("userToken", secret.Key);
        Assert.False(secret.Required);
        Assert.True(secret.Sensitive);
        Assert.Contains("debug info", secret.HelpText!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("works without", secret.HelpText!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("not_required", Assert.Single(musixmatch.RuntimeCapabilities).Configuration);
        Assert.Contains("lyrics", musixmatch.Categories);
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
    public async Task AdministratorSchema_ExposesIndependentSimilarSongFallbackSettings()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: true);

        var schema = Assert.IsType<AdminUiSchemaResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetSchema()).Value);
        var general = Assert.Single(schema.ConfigSections, item => item.Id == "general");

        Assert.Collection(
            general.Fields.Where(item => item.Key.StartsWith("PLAYBACK_", StringComparison.Ordinal)),
            lastFm =>
            {
                Assert.Equal("PLAYBACK_LASTFM_SIMILAR_ENABLED", lastFm.Key);
                Assert.Equal("toggle", lastFm.Type);
                Assert.Equal("playback.lastFmSimilarEnabled", lastFm.ValuePath);
            },
            listenBrainz =>
            {
                Assert.Equal("PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED", listenBrainz.Key);
                Assert.Equal("toggle", listenBrainz.Type);
                Assert.Equal("playback.listenBrainzSimilarEnabled", listenBrainz.ValuePath);
            });
    }

    [Fact]
    public async Task AdministratorSchema_UsesHouseholdProviderPolicy()
    {
        var orders = ProviderOrderPolicyCatalog.Definitions.ToImmutableDictionary(
            item => item.Capability,
            item => (item.Capability == ProviderCapabilityKind.Streaming
                ? new[] { "qobuz", "deezer" }
                : item.DefaultValue.Split(',')).ToImmutableArray());
        var policy = new EffectiveProviderPolicySnapshot(
            orders,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "deezer"),
            "CdLossless",
            0.07);
        using var factory = new AllstarrFactory("Jellyfin", effectivePolicy: policy);
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: true);

        var result = Assert.IsType<OkObjectResult>(await controller.GetSchema());
        var schema = Assert.IsType<AdminUiSchemaResponse>(result.Value);
        var streaming = Assert.Single(schema.PriorityGroups, item => item.Id == "streaming");

        Assert.Equal(["qobuz", "deezer"], streaming.Providers.Take(2));
        Assert.Equal("disabled", Assert.Single(schema.Providers, item => item.Id == "deezer").Status);
        var lyrics = Assert.Single(schema.PriorityGroups, item => item.Id == "lyrics");
        Assert.Null(lyrics.PinnedProvider);
        Assert.Equal("backend", lyrics.Providers[0]);
        Assert.Equal("kept", lyrics.Providers[1]);
        Assert.DoesNotContain(schema.Providers, item => item.Id is "backend" or "kept");
        Assert.NotNull(streaming.PinnedProvider);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdministratorSchema_IncludesActiveExtensionCapabilities(bool searchable)
    {
        using var factory = new AllstarrFactory("Jellyfin");
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IDynamicProviderRegistry>();
        registry.RegisterOrReplaceExtension(new ProviderRegistration(
            new ProviderDescriptor("fixture-extension", "Fixture Extension", "Fixture provider",
                ProviderOrigin.Extension, "1", "1.0",
                [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata,
                    ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Optional, "1.0",
                    searchable ? ["searchTracks", "getTrack"] : ["getTrack"], [ProviderAccountScope.Personal])],
                new ProviderPermissionDescriptor(), entryPoint: "index.js"),
            [new FixtureExtensionMetadata()]));
        var controller = ActivatorUtilities.CreateInstance<AdminUiController>(scope.ServiceProvider);
        controller.ControllerContext = Context(administrator: true);

        var result = Assert.IsType<OkObjectResult>(await controller.GetSchema());
        var schema = Assert.IsType<AdminUiSchemaResponse>(result.Value);
        var provider = Assert.Single(schema.Providers, item => item.Id == "fixture-extension");
        Assert.Equal("Fixture provider", provider.Description);
        Assert.Contains("metadata", provider.Categories);
        var metadata = Assert.Single(schema.PriorityGroups, item => item.Id == "metadata");
        Assert.Equal(searchable, metadata.Providers.Contains(provider.Id));
    }

    [Fact]
    public void Host_ResolvesPaxsenixRegistrationWithRecordingIdentities()
    {
        using var factory = new AllstarrFactory("Jellyfin");
        var registry = factory.Services.GetRequiredService<IProviderRegistry>();
        var registration = Assert.Single(factory.Services.GetServices<ProviderRegistration>(),
            item => item.Descriptor.Id == "paxsenix");
        var lyrics = factory.Services.GetRequiredService<allstarr.Core.Providers.Lyrics.PaxsenixCapabilityAdapter>();

        Assert.Equal(ProviderAccountRequirement.None, Assert.Single(registration.Descriptor.Capabilities).AccountRequirement);
        Assert.Same(lyrics, registry.GetRequiredCapability<IProviderLyricsCapability>("paxsenix", ProviderCapabilityKind.Lyrics));
        Assert.IsType<allstarr.Core.Matching.TrackIdentityService>(
            factory.Services.GetRequiredService<allstarr.Core.Matching.ITrackIdentityService>());
        Assert.IsType<allstarr.Core.Providers.AppleMusicKit.AppleMusicKitMetadataCapabilityAdapter>(
            factory.Services.GetRequiredService<allstarr.Core.Providers.AppleMusicKit.AppleMusicKitMetadataCapabilityAdapter>());
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

        Assert.Equal(TimeSpan.FromSeconds(5), clients.CreateClient("Lrclib").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(6), clients.CreateClient("Odesli").Timeout);
    }

    private static ControllerContext Context(bool administrator)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            AllstarrUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            BackendInstanceId = "primary",
            UserId = "fixture",
            UserName = "fixture",
            IsAdministrator = administrator,
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
                allowedAccountScopes: [ProviderAccountScope.Personal]),
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
        private readonly bool _allowConnections;
        private readonly string _releaseProfile;
        private readonly EffectiveProviderPolicySnapshot? _effectivePolicy;
        private readonly SqliteTestDatabase _database;
        private readonly string _extensionDirectory;

        public AllstarrFactory(
            string backend,
            bool allowConnections = true,
            string releaseProfile = "core",
            EffectiveProviderPolicySnapshot? effectivePolicy = null,
            bool pendingBackend = false)
        {
            _database = Task.Run(() => pendingBackend ? SqliteTestDatabase.CreateAsync() :
                SqliteTestDatabase.CreateConfiguredBackendAsync(backend)).GetAwaiter().GetResult();
            _extensionDirectory = Path.Combine(_database.StorageOptions.DataDirectory, "extensions");
            _backend = backend;
            _allowConnections = allowConnections;
            _releaseProfile = releaseProfile;
            _effectivePolicy = effectivePolicy;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Storage:DataDirectory", Directory.GetParent(_extensionDirectory)!.FullName);
            builder.UseSetting("Backend:Type", _backend);
            builder.UseSetting("Release:Profile", _releaseProfile);
            builder.UseSetting(
                "ProviderAccounts:ListenersCanConnectOwnAccounts",
                _allowConnections.ToString());
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backend:Type"] = _backend,
                    ["Release:Profile"] = _releaseProfile,
                    ["ProviderAccounts:ListenersCanConnectOwnAccounts"] = _allowConnections.ToString(),
                    ["SpotifyApi:Enabled"] = "false",
                    ["SpotifyImport:Enabled"] = "false",
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
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(_effectivePolicy);
                resolver.Setup(item => item.ResolveForUserAsync(
                        It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Guid userId, CancellationToken _) => _effectivePolicy with { UserId = userId });
                services.AddSingleton(resolver.Object);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_database.StorageOptions.DataDirectory))
            {
                _database.ClearHostPool();
                _database.DisposeAsync().GetAwaiter().GetResult();
            }
        }
    }
}
