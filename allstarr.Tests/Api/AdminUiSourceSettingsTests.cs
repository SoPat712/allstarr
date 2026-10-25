using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Settings;
using allstarr.Models.Admin;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AdminUiSourceSettingsTests
{
    [Fact]
    public async Task SpicySchemaHasRequiredMaskedAccountKeyAndOfficialLogo()
    {
        using var http = new HttpClient(new SpicyLyricsTestHandler("unused"));
        var registry = new ProviderRegistry([allstarr.Core.Providers.Lyrics.SpicyLyricsCapabilityAdapter.CreateRegistration(new(http, new SpicyLyricsTestSecrets()))]);
        var configuration = new ConfigurationBuilder().Build();
        using var services = new ServiceCollection().AddSingleton<IProviderRegistry>(registry).BuildServiceProvider();
        var manager = new ProviderStatusManager(configuration, Mock.Of<IHttpClientFactory>(), NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()), services: services);
        var controller = new AdminUiController(configuration, Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()), Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, manager,
            new ProviderAccountOptions(), null!, providerRegistry: registry)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        { SessionId = "fixture", UserId = "fixture", UserName = "Fixture", JellyfinAccessToken = "fixture", IsAdministrator = true, AllstarrUserId = Guid.NewGuid(), ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
        var schema = Assert.IsType<AdminUiSchemaResponse>(Assert.IsType<OkObjectResult>(await controller.GetSchema()).Value);
        var source = Assert.Single(schema.Providers, item => item.Id == "spicylyrics");
        Assert.Equal("/images/providers/spicylyrics.ico", source.LogoUrl);
        var field = Assert.Single(source.AccountSettings);
        Assert.True(field.Sensitive);
        Assert.True(field.Required);
        Assert.Equal("password", field.Type);
        Assert.Equal("needs_config", source.Status);
    }

    [Theory]
    [InlineData(ProviderUseMode.Automatic, "cookie", "configured")]
    [InlineData(ProviderUseMode.ManualOnly, "cookie", "configured")]
    [InlineData(ProviderUseMode.Off, "cookie", "disabled")]
    [InlineData(ProviderUseMode.Automatic, "", "needs_config")]
    public async Task SpotifySchemaUsesSourcePolicyAndAccountReadiness(
        ProviderUseMode mode, string cookie, string expectedStatus)
    {
        var spotify = new SpotifyApiSettings { Enabled = false, SessionCookie = cookie };
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "Lossless", 0)
        {
            ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("spotify", mode)
                .Add("musicbrainz", mode)
        };
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);
        var configuration = new ConfigurationBuilder().Build();
        var manager = new ProviderStatusManager(configuration, Mock.Of<IHttpClientFactory>(),
            NullLogger<ProviderStatusManager>.Instance, Options.Create(spotify),
            Options.Create(new AppleDownloadSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()));
        using var services = new ServiceCollection().BuildServiceProvider();
        var controller = new AdminUiController(configuration, Options.Create(spotify), Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()), Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()),
            null!, manager, new ProviderAccountOptions(), null!, effectivePolicies: policies.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } }
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "fixture",
            UserName = "Fixture",
            JellyfinAccessToken = "fixture",
            IsAdministrator = true,
            AllstarrUserId = Guid.CreateVersion7(),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var schema = Assert.IsType<AdminUiSchemaResponse>(Assert.IsType<OkObjectResult>(await controller.GetSchema()).Value);
        var provider = Assert.Single(schema.Providers, item => item.Id == "spotify");
        Assert.Equal(expectedStatus, provider.Status);
        Assert.DoesNotContain(provider.ConfigSchema, field => field.Key == "SPOTIFY_API_ENABLED");
        Assert.Equal(new[] { "SPOTIFY_API_CACHE_DURATION_MINUTES", "SPOTIFY_API_PREFER_ISRC_MATCHING" },
            provider.ConfigSchema.Where(field => !field.Advanced).Select(field => field.Key));
        var advanced = provider.ConfigSchema.Where(field => field.Advanced).ToArray();
        Assert.Equal(6, advanced.Length);
        Assert.All(advanced, field => Assert.False(string.IsNullOrWhiteSpace(JsonSerializer.Deserialize<string>(field.DefaultValueJson!))));
        var musicBrainz = Assert.Single(schema.Providers, item => item.Id == "musicbrainz");
        Assert.Equal(mode == ProviderUseMode.Off ? "disabled" : "available", musicBrainz.Status);
        Assert.DoesNotContain(schema.Providers.SelectMany(item => item.ConfigSchema),
            field => field.Key is "SPOTIFY_API_ENABLED" or "MUSICBRAINZ_ENABLED");
    }
}
