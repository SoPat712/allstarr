using System.Data.Common;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class EncryptedSecretStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountLease_RechecksRevisionAndRevocationBeforeReturning(bool revoked)
    {
        var store = CreateStore();
        var id = Guid.CreateVersion7();
        var purpose = $"provider-account:lastfm:{id:N}";
        var reference = await store.StoreAsync(_userId, purpose, Encoding.UTF8.GetBytes("old-fixture"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.Add(new()
            {
                Id = id,
                OwnerUserId = _userId,
                ProviderId = "lastfm",
                DisplayName = "Fixture",
                Enabled = true,
                Revision = 1,
                SecretReferenceId = reference.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var pause = new PauseFirstAccountRead(revoked ? "secret_versions" : "provider_accounts");
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options).AddInterceptors(pause).Options);
        var options = new SecretStoreOptions { KeyRingPath = _keyRingPath };
        var reader = new EncryptedSecretStore(factory, new FileSecretKeyRingProvider(options), options, new allstarr.Core.Operations.SystemPlatformClock());
        var snapshot = new ProviderAccountContext(id, "lastfm", ProviderAccountScope.Personal, 1, ownerUserId: _userId, secretReferenceId: reference.Id);
        var opening = Task.Run(async () => { using var lease = await reader.OpenProviderAccountAsync(snapshot); return lease.ReadUtf8(); });
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (revoked) await store.RevokeAsync(reference.Id, new(_userId, purpose));
            else
            {
                await using var db = await _factory.CreateDbContextAsync();
                await using var transaction = await db.Database.BeginTransactionAsync();
                await store.StoreWithinTransactionAsync(db, _userId, purpose, Encoding.UTF8.GetBytes("new-fixture"), reference.Id);
                (await db.ProviderAccounts.SingleAsync()).Revision = 2;
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
        }
        finally { pause.Release.TrySetResult(); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => opening);
        if (revoked) return;
        using var current = await store.OpenProviderAccountAsync(new(id, "lastfm", ProviderAccountScope.Personal, 2,
            ownerUserId: _userId, secretReferenceId: reference.Id));
        Assert.Equal("new-fixture", current.ReadUtf8());
    }

    private sealed class PauseFirstAccountRead(string table) : DbCommandInterceptor
    {
        private int intercepted;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal) && Interlocked.Exchange(ref intercepted, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
