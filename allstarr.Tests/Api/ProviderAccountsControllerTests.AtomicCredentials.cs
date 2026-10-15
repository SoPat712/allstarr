using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed partial class ProviderAccountsControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialPublication_RollsBackSecretAndAccountTogether(bool signIn)
    {
        var account = await AtomicAccountAsync();
        var failing = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(new FailAccountWrite()).Options);
        if (signIn)
        {
            var response = Assert.IsType<ObjectResult>(await AuthenticationController(failing).AuthenticateLastFm(new()
            { AccountId = account.Id, Username = "fixture-user", Password = "fixture-password" }));
            Assert.Equal(500, response.StatusCode);
        }
        else
        {
            var controller = new ProviderAccountsController(failing, _secretStore, _cache, new ProviderAccountOptions())
            { ControllerContext = Controller(Session(_userId)).ControllerContext };
            using var replacement = JsonDocument.Parse("{\"sessionKey\":\"new-fixture-session\"}");
            await Assert.ThrowsAsync<DbUpdateException>(() => controller.ReplaceSecret(account.Id, new() { Secret = replacement.RootElement }));
        }
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, (await db.ProviderAccounts.SingleAsync()).Revision);
        Assert.Equal(1, (await db.SecretReferences.SingleAsync()).ActiveVersion);
        Assert.Single(await db.SecretVersions.ToArrayAsync());
        using var lease = await _secretStore.OpenAsync(account.SecretReferenceId!.Value,
            new SecretAccessContext(_userId, $"provider-account:lastfm:{account.Id:N}"));
        Assert.Contains("old-fixture-session", lease.ReadUtf8());
    }

    [Fact]
    public async Task LastFmSignIn_PublishesSessionRevisionAndAuditTogether()
    {
        var account = await AtomicAccountAsync();
        var response = await AuthenticationController(_factory).AuthenticateLastFm(new()
        { AccountId = account.Id, Username = "fixture-user", Password = "fixture-password" });
        Assert.IsType<OkObjectResult>(response);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, (await db.ProviderAccounts.SingleAsync()).Revision);
        Assert.Equal(2, (await db.SecretReferences.SingleAsync()).ActiveVersion);
        Assert.Single(await db.AuditEvents.Where(x => x.Action == "provider-account.session-replaced").ToArrayAsync());
        using var lease = await _secretStore.OpenAsync(account.SecretReferenceId!.Value,
            new SecretAccessContext(_userId, $"provider-account:lastfm:{account.Id:N}"));
        var value = lease.ReadUtf8();
        Assert.Contains("new-fixture-session", value);
        Assert.DoesNotContain("fixture-password", value);
    }

    [Fact]
    public async Task LastFmSignIn_DoesNotAttachAResponseToAnAccountChangedDuringAuthentication()
    {
        var account = await AtomicAccountAsync();
        var controller = AuthenticationController(_factory, async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            var changed = await db.ProviderAccounts.SingleAsync();
            changed.DisplayName = "Changed during sign-in";
            changed.Revision++;
            await db.SaveChangesAsync();
        });
        Assert.IsType<ConflictObjectResult>(await controller.AuthenticateLastFm(new()
        { AccountId = account.Id, Username = "fixture-user", Password = "fixture-password" }));
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, (await verify.ProviderAccounts.SingleAsync()).Revision);
        Assert.Equal(1, (await verify.SecretReferences.SingleAsync()).ActiveVersion);
        Assert.Single(await verify.SecretVersions.ToArrayAsync());
    }

    private async Task<ProviderAccountRecord> AtomicAccountAsync()
    {
        using var secret = JsonDocument.Parse("{\"apiKey\":\"fixture-key\",\"sharedSecret\":\"fixture-secret\",\"username\":\"fixture-user\",\"sessionKey\":\"old-fixture-session\"}");
        Assert.IsType<CreatedAtActionResult>(await Controller(Session(_userId)).Create(new()
        { ProviderId = "lastfm", Scope = "Personal", Enabled = true, Secret = secret.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ProviderAccounts.AsNoTracking().SingleAsync();
    }

    private ScrobblingAdminController AuthenticationController(IDbContextFactory<AllstarrDbContext> factory, Func<Task>? beforeResponse = null) => new(
        Options.Create(new ScrobblingSettings()), new SessionHttpFactory(beforeResponse), NullLogger<ScrobblingAdminController>.Instance,
        factory, new EncryptedProviderAccountSecretAccessor(_secretStore), _secretStore)
    { ControllerContext = Controller(Session(_userId)).ControllerContext };

    private sealed class SessionHttpFactory(Func<Task>? beforeResponse) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SessionHandler(beforeResponse));
    }
    private sealed class SessionHandler(Func<Task>? beforeResponse) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (beforeResponse != null) await beforeResponse();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<lfm status=\"ok\"><session><name>fixture-user</name><key>new-fixture-session</key></session></lfm>") };
        }
    }
    private sealed class FailAccountWrite : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ProviderAccountRecord>().Any(x => x.State == EntityState.Modified))
                throw new DbUpdateException("Fixture account write interruption");
            return ValueTask.FromResult(result);
        }
    }
}
