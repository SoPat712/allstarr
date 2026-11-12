using allstarr.Core.Providers;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderAccountsControllerTests
{
    [Fact]
    public async Task ManualAccountTestRecordsRejectedSignInForRestart()
    {
        using var secret = JsonDocument.Parse("{\"token\":\"fixture-token\"}");
        Assert.IsType<CreatedAtActionResult>(await Controller(Session(_userId)).Create(new()
        { ProviderId = "listenbrainz", Secret = secret.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.SingleAsync();
        using var handler = new AppleProviderTestFactory.Handler(_ => new(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"valid\":false}") });
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var configuration = new ConfigurationBuilder().Build();
        ProviderStatusManager Manager() => new(configuration, clients.Object, NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()));
        var status = Manager();
        var history = new ProviderCheckHistory(_factory, TimeProvider.System);
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(_factory)
            .AddSingleton(status).AddSingleton(history).AddSingleton(_secretStore).BuildServiceProvider();
        var connection = new ProviderConnectionController(NullLogger<ProviderConnectionController>.Instance)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        connection.HttpContext.Items[allstarr.Services.Admin.AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId, administrator: true);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(
            await connection.TestProvider("listenbrainz", "scrobbling", account.Id)).Value));
        Assert.Equal("account-needs-reauthentication", result.RootElement.GetProperty("reasonCode").GetString());
        var restored = Manager();
        await new ProviderAccountCheckRunner(_factory, restored, history, configuration, TimeProvider.System,
            NullLogger<ProviderAccountCheckRunner>.Instance).RestoreAsync(default);
        Assert.True(restored.GetAccountSignInStatus(account.ProviderId, account.Id, account.Revision).SignInExpired);
    }

    [Fact]
    public async Task SignInExpiry_StatusAndHomeExposeOnlyManageableAccounts()
    {
        var accounts = new[]
        {
            new ProviderAccountRecord { Id = Guid.NewGuid(), ProviderId = "listenbrainz", DisplayName = "Shared", Enabled = true, Revision = 1 },
            new ProviderAccountRecord { Id = Guid.NewGuid(), ProviderId = "listenbrainz", DisplayName = "Own", OwnerUserId = _userId, Enabled = true, Revision = 1 },
            new ProviderAccountRecord { Id = Guid.NewGuid(), ProviderId = "listenbrainz", DisplayName = "Other", OwnerUserId = _otherUserId, Enabled = true, Revision = 1 },
            new ProviderAccountRecord { Id = Guid.NewGuid(), ProviderId = "listenbrainz", DisplayName = "Unchecked", Enabled = true, Revision = 1 }
        };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.AddRange(accounts);
            await db.SaveChangesAsync();
        }
        var configuration = new ConfigurationBuilder().Build();
        var status = new ProviderStatusManager(configuration, Mock.Of<IHttpClientFactory>(),
            NullLogger<ProviderStatusManager>.Instance, Options.Create(new SpotifyApiSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()));
        var checkedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var account in accounts.Take(3))
        {
            var key = ProviderRuntimeStatusKey.CreateManaged(account.ProviderId, "scrobbling", account.Id);
            status.RestoreCheckResult(key, account.Revision, status.GetManagedStatus(key.Provider, key.Capability, account.Id,
                new Dictionary<string, string>(), account.Revision) with
            { Configuration = ProviderConfigurationState.Configured, Health = ProviderHealthState.Degraded, TestedAt = checkedAt, ReasonCode = "account-needs-reauthentication" });
        }
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(_factory)
            .AddSingleton(status).AddSingleton(new DurablePlaylistProjectionReader(_factory, null!)).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Items[allstarr.Services.Admin.AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId, administrator: true);
        var connection = new ProviderConnectionController(NullLogger<ProviderConnectionController>.Instance)
        { ControllerContext = new() { HttpContext = http } };
        using var rows = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await connection.GetProvidersStatus()).Value));
        foreach (var row in rows.RootElement.EnumerateArray().Where(row => row.GetProperty("providerAccountId").ValueKind != JsonValueKind.Null))
        {
            var expired = row.GetProperty("providerAccountId").GetGuid() != accounts[3].Id;
            Assert.Equal(expired, row.GetProperty("signInExpired").GetBoolean());
            if (expired) Assert.Equal(checkedAt, row.GetProperty("signInCheckedAt").GetDateTimeOffset());
            else Assert.Equal(JsonValueKind.Null, row.GetProperty("signInCheckedAt").ValueKind);
        }
        var matches = new TrackMatchCommandService(_factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(_factory),
            new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var overview = new AdminUiController(configuration,
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, status,
            new ProviderAccountOptions(), matches)
        { ControllerContext = new() { HttpContext = http } };
        using var summaries = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await overview.GetProviderSummaries()).Value));
        Assert.Equal(accounts[0].Id, Assert.Single(summaries.RootElement.GetProperty("signInExpiredAccounts").EnumerateArray()).GetProperty("accountId").GetGuid());
        http.Items[allstarr.Services.Admin.AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId);
        using var home = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await overview.GetHome()).Value));
        var own = Assert.Single(home.RootElement.GetProperty("signInExpiredAccounts").EnumerateArray());
        Assert.Equal(accounts[1].Id, own.GetProperty("accountId").GetGuid());
        Assert.Equal("listenbrainz", own.GetProperty("providerId").GetString());
        Assert.Equal("Own", own.GetProperty("accountName").GetString());
    }

    [Fact]
    public async Task SavingAnAccountRequestsAnImmediateCheckForThatAccount()
    {
        var requests = new Mock<IProviderCheckRequests>();
        await using var services = new ServiceCollection().AddSingleton(requests.Object).BuildServiceProvider();
        var controller = Controller(Session(_userId, administrator: true));
        controller.HttpContext.RequestServices = services;
        using var secret = JsonDocument.Parse("{\"token\":\"fixture-token\"}");
        Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        { ProviderId = "listenbrainz", Secret = secret.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.SingleAsync();
        requests.Verify(item => item.Request("listenbrainz", account.Id), Times.Once);
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(account.Id, new() { Secret = secret.RootElement }));
        requests.Verify(item => item.Request("listenbrainz", account.Id), Times.Exactly(2));
        Assert.IsType<OkObjectResult>(await controller.UpdateAudience(account.Id, new() { Scope = "Personal" }));
        requests.Verify(item => item.Request("listenbrainz", account.Id), Times.Exactly(3));
        Assert.IsType<OkObjectResult>(await controller.SetEnabled(account.Id, new() { Enabled = false }));
        requests.Verify(item => item.Request("listenbrainz", account.Id), Times.Exactly(4));
        Assert.IsType<OkObjectResult>(await controller.SetEnabled(account.Id, new() { Enabled = true }));
        requests.Verify(item => item.Request("listenbrainz", account.Id), Times.Exactly(5));
    }
}
