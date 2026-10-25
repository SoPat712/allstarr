using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
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
    public async Task SpicyAccountPrivacyRotationAndExactPurposeUseExistingStorage()
    {
        using var http = new HttpClient(new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static")));
        var source = new SpicyLyricsCapabilityAdapter(http, new EncryptedProviderAccountSecretAccessor(_secretStore));
        var registry = new ProviderRegistry([SpicyLyricsCapabilityAdapter.CreateRegistration(source)]);
        var controller = Controller(Session(_userId), providerRegistry: registry);
        using var input = JsonDocument.Parse("""{"apiKey":"sl_sk_fixture_key"}""");
        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        { ProviderId = "spicylyrics", Scope = "Personal", Secret = input.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.AsNoTracking().SingleAsync();
        Assert.Equal("{}", account.SettingsJson);
        Assert.DoesNotContain("sl_sk_fixture_key", JsonSerializer.Serialize(created.Value));
        var listed = Assert.IsType<OkObjectResult>(await controller.List());
        Assert.DoesNotContain("sl_sk_fixture_key", JsonSerializer.Serialize(listed.Value));
        Assert.DoesNotContain("sl_sk_fixture_key", Encoding.UTF8.GetString((await db.SecretVersions.SingleAsync()).Ciphertext));
        using var replacement = JsonDocument.Parse("""{"apiKey":"sl_sk_rotated_fixture"}""");
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(account.Id, new() { Secret = replacement.RootElement }));
        var current = await db.ProviderAccounts.AsNoTracking().SingleAsync();
        Assert.True(current.Revision > account.Revision);
        var accessor = new EncryptedProviderAccountSecretAccessor(_secretStore);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accessor.UseAsync(SpicyAccountContext(account), _ => Task.FromResult(true), default));
        Assert.True(await accessor.UseAsync(SpicyAccountContext(current), bytes => Task.FromResult(Encoding.UTF8.GetString(bytes.Span).Contains("sl_sk_rotated_fixture", StringComparison.Ordinal)), default));
        var other = Assert.IsType<OkObjectResult>(await Controller(Session(_otherUserId), providerRegistry: registry).List());
        Assert.DoesNotContain(account.Id.ToString(), JsonSerializer.Serialize(other.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("get", "rotate", false)]
    [InlineData("http", "revoke", false)]
    [InlineData("set", "disable", false)]
    [InlineData("set", "rotate", true)]
    public async Task SpicyRealCredentialStoreFencesEveryHeldPublication(string phase, string change, bool failWrite)
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var cache = new SpicyLyricsTestCache();
        var source = new SpicyLyricsCapabilityAdapter(http, new EncryptedProviderAccountSecretAccessor(_secretStore), cache);
        var registry = new ProviderRegistry([SpicyLyricsCapabilityAdapter.CreateRegistration(source)]);
        var controller = Controller(Session(_userId), providerRegistry: registry);
        using var input = JsonDocument.Parse("""{"apiKey":"sl_sk_fixture_key"}""");
        Assert.IsType<CreatedAtActionResult>(await controller.Create(new() { ProviderId = "spicylyrics", Scope = "Personal", Secret = input.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.AsNoTracking().SingleAsync();
        var execution = SpicyExecution(account);
        if (phase == "get") Assert.True((await source.FetchLyricsAsync(execution, SpicyLyricsTestData.Request())).IsSuccess);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> hold = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (failWrite) throw new IOException("Fixture cache interruption");
        };
        if (phase == "get") cache.BeforeGet = hold;
        else if (phase == "set") cache.BeforeSet = hold;
        else handler.BeforeResponse = _ => hold();
        var pending = source.FetchLyricsAsync(execution, SpicyLyricsTestData.Request());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (change == "rotate")
            {
                using var replacement = JsonDocument.Parse("""{"apiKey":"sl_sk_rotated_fixture"}""");
                Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(account.Id, new() { Secret = replacement.RootElement }));
            }
            else
            {
                await using var mutation = await _factory.CreateDbContextAsync();
                if (change == "disable") (await mutation.ProviderAccounts.SingleAsync()).Enabled = false;
                else (await mutation.SecretReferences.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow;
                await mutation.SaveChangesAsync();
            }
            release.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error!.Kind);
            if (phase == "set") Assert.Empty(cache.Values);
            var reads = cache.Reads;
            await source.FetchLyricsAsync(execution, SpicyLyricsTestData.Request());
            Assert.Equal(reads, cache.Reads);
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData("Personal")]
    [InlineData("Shared")]
    public async Task SpicyManualAndScheduledChecksReuseRealDiagnosticAccountAuthorization(string scope)
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static")) { MatchRequestedId = true };
        using var http = new HttpClient(handler);
        var cache = new SpicyLyricsTestCache();
        var source = new SpicyLyricsCapabilityAdapter(http, new EncryptedProviderAccountSecretAccessor(_secretStore), cache);
        var registry = new ProviderRegistry([SpicyLyricsCapabilityAdapter.CreateRegistration(source)]);
        var controller = Controller(Session(_userId, administrator: true), providerRegistry: registry);
        using var input = JsonDocument.Parse("""{"apiKey":"sl_sk_fixture_key"}""");
        Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        { ProviderId = "spicylyrics", Scope = scope, OwnerUserId = scope == "Personal" ? _otherUserId : null, Secret = input.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var admin = await db.Users.SingleAsync(item => item.Id == _userId);
        admin.IsAdmin = true;
        await db.SaveChangesAsync();
        var account = await db.ProviderAccounts.AsNoTracking().SingleAsync();
        var runtime = new ProviderRuntimeHealth();
        var resolver = new DurableProviderRouteAccountResolver(new ProviderAccountResolver(_factory), new RuntimeProviderRouteHealthSource(runtime), registry);
        var factory = new ProviderDiagnosticExecutionFactory(registry, resolver);
        var actor = new ProviderActorContext(ProviderActorKind.Administrator, _userId, new(admin.BackendType, admin.BackendInstanceId, admin.BackendPrincipalId));
        var prepared = await factory.CreateAsync(actor, "spicylyrics", ProviderCapabilityKind.Lyrics, account.Id,
            "check", DateTimeOffset.UtcNow.AddSeconds(30), default);
        Assert.NotNull(prepared.Context);
        Assert.Equal(account.Id, prepared.Context.Account!.AccountId);
        Assert.Equal(scope == "Personal" ? _otherUserId : _userId, prepared.Context.Actor.EffectiveUserId);
        Assert.True((await source.FetchLyricsAsync(prepared.Context, SpicyLyricsTestData.Request())).IsSuccess);
        var reads = cache.Reads;
        var helper = new SpicyLyricsHealthCapabilityAdapter(source);
        var operations = new ProviderTryOperations(registry, helper);
        Assert.True((await operations.ProbeAsync(prepared.Context, registry.GetRequired("spicylyrics"), ProviderCapabilityKind.Lyrics)).Succeeded);
        Assert.Equal(reads, cache.Reads);
        using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(_factory)
            .AddSingleton<IProviderRegistry>(registry).AddSingleton(factory).AddSingleton(operations).BuildServiceProvider();
        var manager = new ProviderStatusManager(new ConfigurationBuilder().Build(), Mock.Of<IHttpClientFactory>(), NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()), Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()), health: runtime, services: services);
        var values = new Dictionary<string, string> { ["apikey"] = "sl_sk_fixture_key" };
        var result = await manager.TestManagedProviderCapabilityAsync("spicylyrics", "lyrics", account.Id, values, accountRevision: account.Revision);
        Assert.Equal(ProviderHealthState.Healthy, result.Health);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(reads, cache.Reads);
        var history = new ProviderCheckHistory(_factory, TimeProvider.System);
        var runner = new ProviderAccountCheckRunner(_factory, manager, history, new ConfigurationBuilder().Build(), TimeProvider.System,
            NullLogger<ProviderAccountCheckRunner>.Instance, _secretStore, registry);
        Assert.True(await runner.SweepAsync(default, "spicylyrics"));
        Assert.Equal(4, handler.Calls);
        Assert.Equal(reads, cache.Reads);
        var saved = Assert.Single(await history.ReadLatestAsync(default));
        Assert.Equal(account.Id, saved.Key.ProviderAccountId);
        Assert.Equal(account.Revision, saved.Revision);
        Assert.False(manager.CanTestAccountFreeCapability("spicylyrics", "lyrics"));
        Assert.DoesNotContain(manager.GetAllAccountFreeStatuses(), item => item.Provider == "spicylyrics");
        manager.RestoreCheckResult(ProviderRuntimeStatusKey.CreateManaged("spicylyrics", "lyrics", account.Id), account.Revision, result);
        Assert.Null(manager.GetManagedStatus("spicylyrics", "lyrics", account.Id, values, account.Revision + 1).TestedAt);
    }

    private static ProviderAccountContext SpicyAccountContext(ProviderAccountRecord account) => new(account.Id, account.ProviderId,
        account.Scope, account.Revision, account.Enabled, account.OwnerUserId, secretReferenceId: account.SecretReferenceId);
    private ProviderExecutionContext SpicyExecution(ProviderAccountRecord account) => new(
        new(ProviderActorKind.User, _userId, new("jellyfin", "fixture", "a")), "spicylyrics", SpicyAccountContext(account),
        SpicyLyricsTestData.Context().Policy, "lyrics", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
}
