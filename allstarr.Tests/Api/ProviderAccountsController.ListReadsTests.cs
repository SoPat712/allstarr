using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed partial class ProviderAccountsControllerTests
{
    private readonly ITestOutputHelper _output;

    public ProviderAccountsControllerTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(1, false, 4)]
    [InlineData(24, false, 4)]
    [InlineData(1, true, 8)]
    [InlineData(24, true, 8)]
    public async Task List_WarmConfiguredAccountsUseBoundedDatabaseReads(
        int accountCount, bool administrator, int expectedReads)
    {
        for (var index = 0; index < accountCount; index++)
            await CreateListReadAccount(_userId, $"Connection {index:D2}");

        var reads = new ListReadCounter();
        var controller = ListReadController(Session(_userId, administrator), reads);
        _ = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.List()).Value);
        reads.Count = 0;
        var elapsed = Stopwatch.StartNew();
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.List()).Value));
        elapsed.Stop();
        _output.WriteLine(
            "Warm provider account list: {0} accounts, administrator={1}, {2:F2} ms, {3} database reads",
            accountCount, administrator, elapsed.Elapsed.TotalMilliseconds, reads.Count);

        Assert.Equal(accountCount, result.RootElement.GetProperty("accounts").GetArrayLength());
        Assert.Equal(expectedReads, reads.Count);
        Assert.DoesNotContain("fixture-token", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_WarmAdministratorHistoryUsesBoundedDatabaseReads()
    {
        const int listenerCount = 16;
        const int playsPerListener = 250;
        const int accountCount = 24;
        var owners = new List<Guid> { _userId, _otherUserId };
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            for (var index = owners.Count; index < listenerCount; index++)
            {
                var id = Guid.CreateVersion7();
                owners.Add(id);
                db.Users.Add(User(id, $"Listener {index:D2}"));
            }
            foreach (var owner in owners)
                for (var index = 0; index < playsPerListener; index++)
                    db.ListeningEvents.Add(ListenerPlay(owner, now.AddMinutes(-index), $"Song {index}"));
            await db.SaveChangesAsync();
        }
        for (var index = 0; index < accountCount; index++)
            await CreateListReadAccount(_userId, $"Connection {index:D2}");

        var reads = new ListReadCounter();
        var controller = ListReadController(Session(_userId, true), reads);
        _ = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.List()).Value);
        var durations = new List<double>();
        var counts = new List<int>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            reads.Count = 0;
            var elapsed = Stopwatch.StartNew();
            using var result = JsonDocument.Parse(JsonSerializer.Serialize(
                Assert.IsType<OkObjectResult>(await controller.List()).Value));
            elapsed.Stop();
            durations.Add(elapsed.Elapsed.TotalMilliseconds);
            counts.Add(reads.Count);
            var listeners = result.RootElement.GetProperty("audienceUsers").EnumerateArray().ToArray();
            Assert.Equal(listenerCount, listeners.Length);
            Assert.All(listeners, listener =>
            {
                Assert.Equal(playsPerListener, listener.GetProperty("playsThisWeek").GetInt32());
                Assert.Equal(Enumerable.Range(0, 5).Select(index => $"Song {index}"),
                    listener.GetProperty("recentPlays").EnumerateArray()
                        .Select(play => play.GetProperty("title").GetString()));
            });
        }
        durations.Sort();
        _output.WriteLine(
            "Warm provider account list: {0} accounts, {1} listeners, {2} history rows, median {3:F2} ms, maximum {4:F2} ms, database reads {5}",
            accountCount, listenerCount, listenerCount * playsPerListener, durations[durations.Count / 2],
            durations[^1], string.Join(", ", counts));
        Assert.All(counts, count => Assert.Equal(8, count));
        Assert.True(durations[durations.Count / 2] < 300, $"Warm provider list median was {durations[durations.Count / 2]:F2} ms.");
    }

    [Fact]
    public async Task List_WarmReadsPreserveAudienceVisibilityAndCredentialRedaction()
    {
        var own = await CreateListReadAccount(_userId, "Own");
        var other = await CreateListReadAccount(_otherUserId, "Other");
        var shared = await CreateListReadAccount(null, "Shared");
        var listener = Controller(Session(_userId));
        Assert.IsType<OkObjectResult>(await listener.List());
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await listener.List()).Value));
        var accounts = result.RootElement.GetProperty("accounts").EnumerateArray()
            .ToDictionary(item => item.GetProperty("Id").GetGuid());
        Assert.Equal(2, accounts.Count);
        Assert.False(accounts.ContainsKey(other.Id));
        Assert.Equal("https://listen.example.test/1/",
            accounts[own.Id].GetProperty("configuration").GetProperty("baseUrl").GetString());
        Assert.Equal(new[] { "token", "baseUrl" }, accounts[own.Id].GetProperty("configuredFields")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.False(accounts[own.Id].GetProperty("configuration").TryGetProperty("token", out _));
        Assert.Empty(accounts[shared.Id].GetProperty("configuration").EnumerateObject());
        Assert.Empty(accounts[shared.Id].GetProperty("configuredFields").EnumerateArray());
        Assert.DoesNotContain("fixture-token", result.RootElement.GetRawText(), StringComparison.Ordinal);

        using var managed = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await Controller(Session(_userId, true)).List()).Value));
        Assert.Equal(3, managed.RootElement.GetProperty("accounts").GetArrayLength());
        Assert.All(managed.RootElement.GetProperty("accounts").EnumerateArray(), account =>
            Assert.Equal("https://listen.example.test/1/",
                account.GetProperty("configuration").GetProperty("baseUrl").GetString()));
        Assert.DoesNotContain("fixture-token", managed.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_WarmReadsObserveReplacementAndRevocation()
    {
        var account = await CreateListReadAccount(_userId, "Own");
        var controller = Controller(Session(_userId));
        Assert.IsType<OkObjectResult>(await controller.List());
        var access = new SecretAccessContext(_userId, $"provider-account:{account.ProviderId}:{account.Id:N}");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        { token = "replacement-fixture-token", baseUrl = "https://replacement.example.test/1/" });
        await _secretStore.StoreAsync(_userId, access.Purpose, bytes, account.SecretReferenceId);
        using var replaced = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.List()).Value));
        var row = Assert.Single(replaced.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.Equal("https://replacement.example.test/1/",
            row.GetProperty("configuration").GetProperty("baseUrl").GetString());
        Assert.Equal(2, row.GetProperty("secret").GetProperty("version").GetInt32());
        Assert.DoesNotContain("fixture-token", row.GetRawText(), StringComparison.Ordinal);

        await _secretStore.RevokeAsync(account.SecretReferenceId!.Value, access);
        using var revoked = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.List()).Value));
        row = Assert.Single(revoked.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.True(row.GetProperty("secret").GetProperty("revoked").GetBoolean());
        Assert.Empty(row.GetProperty("configuration").EnumerateObject());
        Assert.Empty(row.GetProperty("configuredFields").EnumerateArray());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("purpose")]
    public async Task List_WarmReadsRecheckSecretAccess(string mismatch)
    {
        var account = await CreateListReadAccount(_userId, "Own");
        var controller = Controller(Session(_userId));
        Assert.IsType<OkObjectResult>(await controller.List());
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var reference = await db.SecretReferences.SingleAsync(item => item.Id == account.SecretReferenceId);
            if (mismatch == "owner") reference.UserId = _otherUserId;
            else reference.Purpose = "unrelated-purpose";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.List());
    }

    private ProviderAccountsController ListReadController(AdminAuthSession session, ListReadCounter reads)
    {
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(reads).Options);
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(_root, "keyring.json") };
        var store = new EncryptedSecretStore(factory, new FileSecretKeyRingProvider(options), options,
            new SystemPlatformClock());
        return new ProviderAccountsController(factory, store, _cache,
            new ProviderAccountOptions { ListenersCanConnectOwnAccounts = true })
        { ControllerContext = Controller(session).ControllerContext };
    }

    private async Task<ProviderAccountRecord> CreateListReadAccount(Guid? ownerId, string name)
    {
        using var secret = JsonDocument.Parse("""{"token":"fixture-token","baseUrl":"https://listen.example.test/1/"}""");
        var result = Assert.IsType<CreatedAtActionResult>(await Controller(Session(ownerId ?? _userId, ownerId == null))
            .Create(new()
            {
                ProviderId = "listenbrainz",
                DisplayName = name,
                Scope = ownerId.HasValue ? "Personal" : "Shared",
                Secret = secret.RootElement.Clone()
            }));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var id = payload.RootElement.GetProperty("Id").GetGuid();
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ProviderAccounts.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    private sealed class ListReadCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
