using System.Reflection;
using allstarr.Controllers;
using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Admin;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed class PublicListenerAddressTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("https://Music.Example.test/", "https://music.example.test")]
    [InlineData("http://music.example.test:5274/music/", "http://music.example.test:5274/music")]
    [InlineData(" https://music.example.test/prefix/// ", "https://music.example.test/prefix")]
    public async Task ValidAddressIsNormalizedAndHot(string value, string expected)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var service = Settings(database);
        await service.ApplyBatchAsync([new("Gateway:PublicListenerUrl", value)], "fixture");
        Assert.Equal(expected, (await service.GetAsync("Gateway:PublicListenerUrl")).NormalizedValue);
        Assert.False(RuntimeSettingCatalog.Require("Gateway:PublicListenerUrl").RequiresRestart);
        Assert.True(LegacyEnvParser.TryGetDurableAlias("PUBLIC_LISTENER_URL", out var key));
        Assert.Equal("Gateway:PublicListenerUrl", key);
    }

    [Theory]
    [InlineData("ftp://music.example.test")]
    [InlineData("//music.example.test")]
    [InlineData("https://user:pass@music.example.test")]
    [InlineData("https://music.example.test?token=fixture")]
    [InlineData("https://music.example.test#fixture")]
    [InlineData("https://music.example.test\\evil")]
    [InlineData("https://music.example.test/%5Cevil")]
    [InlineData("https://music.example.test/%0aevil")]
    [InlineData("https://music.example.test/\n")]
    public async Task InvalidAddressCannotReplaceStoredValue(string invalid)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var service = Settings(database);
        await service.ApplyBatchAsync([new("Gateway:PublicListenerUrl", "https://music.example.test")], "fixture");
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync([new("Gateway:PublicListenerUrl", invalid, 1)], "fixture"));
        Assert.Equal("https://music.example.test", (await service.GetAsync("Gateway:PublicListenerUrl")).NormalizedValue);
    }

    [Fact]
    public void SchemaHasOneHotFieldAndStartupRecognizesTheAlias()
    {
        var method = typeof(AdminUiController).GetMethod("BuildConfigSections", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sections = Assert.IsType<List<AdminUiConfigSection>>(method.Invoke(null, null));
        var section = Assert.Single(sections, item => item.Id == "client-address");
        var field = Assert.Single(section.Fields);
        Assert.Equal("Music-client address", section.Label);
        Assert.Equal("PUBLIC_LISTENER_URL", field.Key);
        Assert.Equal("operatorSettings.Gateway:PublicListenerUrl", field.ValuePath);
        Assert.False(field.RequiresRestart);
        Assert.Single(sections.SelectMany(item => item.Fields), item => item.Key == "PUBLIC_LISTENER_URL");
        var directory = Path.Combine(Path.GetTempPath(), "allstarr-address-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".env");
        try
        {
            File.WriteAllText(path, "PUBLIC_LISTENER_URL=https://music.example.test/prefix/\n");
            var configuration = new ConfigurationManager();
            RuntimeEnvConfiguration.AddBootstrapOverrides(configuration, path);
            Assert.Equal("https://music.example.test/prefix/", configuration["Gateway:PublicListenerUrl"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("Jellyfin", "")]
    [InlineData("Subsonic", "https://music.example.test/prefix/")]
    public async Task BootstrapUsesCanonicalSessionAndExplicitGatewaySetting(string backend, string configured)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = owner,
                Enabled = true,
                BackendType = backend.ToLowerInvariant(),
                BackendInstanceId = "fixture",
                BackendPrincipalId = "principal",
                DisplayName = "Canonical listener"
            });
            await db.SaveChangesAsync();
        }
        var configuration = new ConfigurationManager();
        configuration["Backend:Type"] = backend;
        configuration["Gateway:PublicListenerUrl"] = configured;
        configuration["Backend:Url"] = "https://native.example.test/private";
        var settings = new DurableRuntimeSettingsService(factory, configuration, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        var reports = new ProblemReportService(factory, new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>(), [], configuration: configuration);
        var controller = new ProblemReportsController(reports, settings)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.Request.Host = new HostString("spoofed.example.test", 5275);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "principal",
            UserName = "Canonical listener",
            IsAdministrator = false,
            AllstarrUserId = owner,
            BackendType = backend,
            BackendInstanceId = "fixture",
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var context = Assert.IsType<ListenerContext>(Assert.IsType<OkObjectResult>(await controller.ListenerContext(default)).Value);
        Assert.Equal(backend, context.Backend);
        Assert.Equal("Canonical listener", context.Username);
        Assert.Equal(configured.Length == 0 ? null : "https://music.example.test/prefix", context.MusicClientUrl);
        Assert.Equal(configured.Length == 0 ? "unconfigured" : "configured", context.AddressSource);
        await settings.ApplyBatchAsync([new("Gateway:PublicListenerUrl", "http://gateway.example.test:5274")], "fixture");
        var changed = Assert.IsType<ListenerContext>(Assert.IsType<OkObjectResult>(await controller.ListenerContext(default)).Value);
        Assert.Equal("http://gateway.example.test:5274", changed.MusicClientUrl);
        var session = (AdminAuthSession)controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey]!;
        Assert.Null((await reports.ContextAsync(session, null, default)).Song);
        configuration["Backend:Type"] = backend == "Jellyfin" ? "Subsonic" : "Jellyfin";
        Assert.Equal(401, Assert.IsType<ObjectResult>(await controller.ListenerContext(default)).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<ProblemReportException>(() => reports.ContextAsync(session, null, default))).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<ProblemReportException>(() => reports.CreateAsync(session,
            new(Guid.NewGuid(), "other", "Fixture"), default))).StatusCode);
    }
    private static DurableRuntimeSettingsService Settings(SqliteTestDatabase database) => new(new Factory(database.Options), new ConfigurationManager(),
        new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    { public AllstarrDbContext CreateDbContext() => new(options); }
}
