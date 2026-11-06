using System.Net;
using System.Text;
using allstarr.Core.Operations;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed partial class EncryptedSecretStoreTests
{
    [Fact]
    public async Task ProviderChecks_UseEachAccountsOwnDecryptedCredentials()
    {
        var store = CreateStore();
        var accountIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        for (var index = 0; index < accountIds.Length; index++)
        {
            var secret = await store.StoreAsync(_userId, $"provider-account:listenbrainz:{accountIds[index]:N}",
                Encoding.UTF8.GetBytes($"{{\"token\":\"fixture-token-{index}\"}}"));
            await using var db = await _factory.CreateDbContextAsync();
            db.ProviderAccounts.Add(new()
            {
                Id = accountIds[index],
                ProviderId = "listenbrainz",
                OwnerUserId = _userId,
                DisplayName = "Fixture",
                Enabled = true,
                Revision = 1,
                SecretReferenceId = secret.Id
            });
            await db.SaveChangesAsync();
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProviderChecks:PauseMilliseconds"] = "0"
        }).Build();
        using var handler = new ProviderCheckCredentialHandler();
        var status = new ProviderStatusManager(configuration, new ProviderCheckHttpFactory(handler),
            NullLogger<ProviderStatusManager>.Instance, Options.Create(new SpotifyApiSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()));
        configuration["MULTI_PROVIDER_DISABLED_PROVIDERS"] = string.Join(",", status.GetAllAccountFreeStatuses()
            .Select(item => item.Provider).Where(provider => provider != "listenbrainz").Distinct());
        var history = new ProviderCheckHistory(_factory, TimeProvider.System);
        var runner = new ProviderAccountCheckRunner(_factory, status, history, configuration,
            TimeProvider.System, NullLogger<ProviderAccountCheckRunner>.Instance, store);
        Assert.True(await runner.SweepAsync(default));
        Assert.Equal(accountIds.Select((id, index) => (Id: id, Token: $"fixture-token-{index}"))
            .OrderBy(item => item.Id).Select(item => item.Token), handler.Tokens);
        var results = await history.ReadLatestAsync(default);
        Assert.Equal(2, results.Count);
        Assert.Equal(accountIds.Order(), results.Select(result => result.Key.ProviderAccountId!.Value).Order());
        Assert.All(results, result => Assert.True(result.Status.IsReady));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync(row => row.Id == accountIds[0]);
            account.SecretReferenceId = null;
            account.Revision++;
            await db.SaveChangesAsync();
        }
        handler.Tokens.Clear();
        Assert.True(await runner.SweepAsync(default));
        Assert.Equal(new[] { "fixture-token-1" }, handler.Tokens);
        results = await history.ReadLatestAsync(default);
        Assert.Equal(2, results.Count);
        var unconfigured = Assert.Single(results, result => result.Key.ProviderAccountId == accountIds[0]);
        Assert.Equal(ProviderConfigurationState.NeedsConfiguration, unconfigured.Status.Configuration);
        Assert.Equal("missing_provider_account_secret", unconfigured.Status.ReasonCode);
        Assert.True(Assert.Single(results, result => result.Key.ProviderAccountId == accountIds[1]).Status.IsReady);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.All(await context.AuditEvents.Where(row => row.Category == ProviderCheckHistory.Category).ToListAsync(),
            row => Assert.DoesNotContain("fixture-token", row.DetailsJson));
    }

    [Fact]
    public void ProviderChecks_ListeningClientsDoNotShareAuthenticationCookies()
    {
        using var handler = ListenBrainzServiceEndpoint.CreateHttpHandler();
        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task RemovedAccounts_AreExcludedFromCheckRestorationAndSweeps()
    {
        var accountId = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.Add(new()
            {
                Id = accountId,
                ProviderId = "listenbrainz",
                OwnerUserId = _userId,
                DisplayName = "Fixture",
                Enabled = true,
                Revision = 1,
                SettingsJson = """{"__removed":true}"""
            });
            await db.SaveChangesAsync();
        }
        var configuration = new ConfigurationBuilder().Build();
        using var handler = new ProviderCheckCredentialHandler();
        var status = new ProviderStatusManager(configuration, new ProviderCheckHttpFactory(handler),
            NullLogger<ProviderStatusManager>.Instance, Options.Create(new SpotifyApiSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()));
        var history = new ProviderCheckHistory(_factory, TimeProvider.System);
        var key = ProviderRuntimeStatusKey.CreateManaged("listenbrainz", ProviderCapabilities.Scrobbling, accountId);
        var previous = status.GetManagedStatus(key.Provider, key.Capability, accountId, new Dictionary<string, string>(), 1)
            with
        {
            Health = ProviderHealthState.Healthy,
            TestedAt = DateTimeOffset.UtcNow
        };
        await history.RecordAsync(new(key, 1, previous), "fixture", default);
        var runner = new ProviderAccountCheckRunner(_factory, status, history, configuration,
            TimeProvider.System, NullLogger<ProviderAccountCheckRunner>.Instance);

        await runner.RestoreAsync(default);

        Assert.Null(status.GetManagedStatus(key.Provider, key.Capability, accountId, new Dictionary<string, string>(), 1).TestedAt);
        Assert.True(await runner.SweepAsync(default, "listenbrainz"));
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await check.AuditEvents.CountAsync(row => row.Category == ProviderCheckHistory.Category));
        Assert.Empty(handler.Tokens);
    }

    private sealed class ProviderCheckHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class ProviderCheckCredentialHandler : HttpMessageHandler
    {
        public List<string> Tokens = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.listenbrainz.org", request.RequestUri!.Host);
            Tokens.Add(request.Headers.Authorization!.Parameter!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"valid\":true}") });
        }
    }
}
