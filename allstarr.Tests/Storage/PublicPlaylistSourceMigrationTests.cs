using System.Data;
using System.Globalization;
using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class PublicPlaylistSourceMigrationTests
{
    private const string Before = "20261014162517_PreservePlaylistDestinationMode";
    private const string Current = "20261014170945_PublicPlaylistSources";

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task PopulatedUpgradePreservesPrivatePlaylistRowsAndForeignKeys()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);

        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var link = Guid.CreateVersion7();
        var snapshot = Guid.CreateVersion7();
        var external = Guid.CreateVersion7();
        await InsertUserAsync(db, user, now);
        await InsertProviderAccountAsync(db, account, user, now);
        await InsertPrivateLinkAsync(db, link, user, account, now);
        await InsertSnapshotAsync(db, snapshot, user, link, account, now, "Private playlist");
        await InsertExternalSnapshotAsync(db, external, user, account, now, "spotify", new string('e', 64));
        var beforeLegacyRows = await SnapshotLegacyRowsAsync(db);

        await db.Database.MigrateAsync();

        Assert.Equal(beforeLegacyRows, await SnapshotLegacyRowsAsync(db));
        Assert.Equal(user.ToString(), await ScalarAsync<string>(db, "SELECT Id AS Value FROM users"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("spotify", await ScalarAsync<string>(db, "SELECT ProviderId AS Value FROM provider_accounts"));
        Assert.Equal(new string('l', 64), await ScalarAsync<string>(db, "SELECT SourcePlaylistIdHash AS Value FROM playlist_links"));
        Assert.Equal("Private playlist", await ScalarAsync<string>(db, "SELECT Name AS Value FROM playlist_source_snapshots"));
        Assert.Equal(new string('e', 64), await ScalarAsync<string>(db, "SELECT ExternalIdHash AS Value FROM external_metadata_snapshots"));
        Assert.Equal(1L, await CountAsync(db, "users"));
        Assert.Equal(1L, await CountAsync(db, "provider_accounts"));
        Assert.Equal(1L, await CountAsync(db, "playlist_links"));
        Assert.Equal(1L, await CountAsync(db, "playlist_source_snapshots"));
        Assert.Equal(1L, await CountAsync(db, "external_metadata_snapshots"));
        Assert.Empty(await ReadRowsAsync(db, "PRAGMA foreign_key_check"));
        Assert.Equal(["integrity_check=ok"], await ReadRowsAsync(db, "PRAGMA integrity_check"));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM provider_accounts WHERE Id = {account}"));
        Assert.Equal(1L, await CountAsync(db, "provider_accounts"));
        Assert.Equal(1L, await CountAsync(db, "playlist_links"));
        Assert.Equal(1L, await CountAsync(db, "playlist_source_snapshots"));
        Assert.Equal(1L, await CountAsync(db, "external_metadata_snapshots"));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task PublicRowsAllowNullAccountsAndEnforceProviderOwnerScopedUniqueness()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);

        var now = DateTimeOffset.UtcNow;
        var ownerA = Guid.CreateVersion7();
        var ownerB = Guid.CreateVersion7();
        var linkA = Guid.CreateVersion7();
        var linkB = Guid.CreateVersion7();
        var linkC = Guid.CreateVersion7();
        var playlistHash = new string('p', 64);
        var externalHash = new string('x', 64);
        await InsertUserAsync(db, ownerA, now, "owner-a");
        await InsertUserAsync(db, ownerB, now, "owner-b");
        await db.Database.MigrateAsync();

        await InsertPublicLinkAsync(db, linkA, ownerA, "spotify", playlistHash, now);
        await InsertSnapshotAsync(db, Guid.CreateVersion7(), ownerA, linkA, account: null, now, "Public playlist A");
        await InsertExternalSnapshotAsync(db, Guid.CreateVersion7(), ownerA, account: null, now, "spotify", externalHash);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await InsertPublicLinkAsync(db, Guid.CreateVersion7(), ownerA, "spotify", playlistHash, now));
        await Assert.ThrowsAsync<SqliteException>(async ()
            => await InsertExternalSnapshotAsync(db, Guid.CreateVersion7(), ownerA, account: null, now, "spotify", externalHash));

        await InsertPublicLinkAsync(db, linkB, ownerB, "spotify", playlistHash, now);
        await InsertPublicLinkAsync(db, linkC, ownerA, "deezer", playlistHash, now);
        await InsertSnapshotAsync(db, Guid.CreateVersion7(), ownerB, linkB, account: null, now, "Public playlist B");
        await InsertSnapshotAsync(db, Guid.CreateVersion7(), ownerA, linkC, account: null, now, "Public playlist C");
        await InsertExternalSnapshotAsync(db, Guid.CreateVersion7(), ownerB, account: null, now, "spotify", externalHash);
        await InsertExternalSnapshotAsync(db, Guid.CreateVersion7(), ownerA, account: null, now, "deezer", externalHash);

        Assert.Equal(3L, await CountAsync(db, "playlist_links"));
        Assert.Equal(3L, await CountAsync(db, "playlist_source_snapshots"));
        Assert.Equal(3L, await CountAsync(db, "external_metadata_snapshots"));
        Assert.Equal(3L, await ScalarAsync<long>(db, "SELECT COUNT(*) AS Value FROM playlist_links WHERE ProviderAccountId IS NULL"));
        Assert.Equal(3L, await ScalarAsync<long>(db, "SELECT COUNT(*) AS Value FROM external_metadata_snapshots WHERE ProviderAccountId IS NULL"));
        Assert.Equal(3L, await ScalarAsync<long>(db, "SELECT COUNT(*) AS Value FROM playlist_source_snapshots WHERE ProviderAccountId IS NULL"));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task DowngradeWithPublicRowsFailsWithoutRemovingRowsOrIndexes()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);

        var now = DateTimeOffset.UtcNow;
        var owner = Guid.CreateVersion7();
        var link = Guid.CreateVersion7();
        await InsertUserAsync(db, owner, now);
        await db.Database.MigrateAsync();
        await InsertPublicLinkAsync(db, link, owner, "spotify", new string('p', 64), now);
        await InsertSnapshotAsync(db, Guid.CreateVersion7(), owner, link, account: null, now, "Public playlist");
        await InsertExternalSnapshotAsync(db, Guid.CreateVersion7(), owner, account: null, now, "spotify", new string('x', 64));

        await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(Before));

        Assert.Equal(Current, await ScalarAsync<string>(db, $"SELECT MigrationId AS Value FROM __EFMigrationsHistory WHERE MigrationId = '{Current}'"));
        Assert.Equal(1L, await CountAsync(db, "playlist_links"));
        Assert.Equal(1L, await CountAsync(db, "playlist_source_snapshots"));
        Assert.Equal(1L, await CountAsync(db, "external_metadata_snapshots"));
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_playlist_link_public_source_target'"));
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_external_snapshot_public_version'"));
    }

    private static async Task InsertUserAsync(AllstarrDbContext db, Guid id, DateTimeOffset now, string displayName = "Listener")
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id,BackendType,BackendInstanceId,BackendPrincipalId,DisplayName,IsAdmin,Enabled,CreatedAt,UpdatedAt,LastSeenAt)
            VALUES ({id},'jellyfin','fixture',{displayName}, {displayName},0,1,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks});
            """);
    }

    private static async Task InsertProviderAccountAsync(AllstarrDbContext db, Guid id, Guid owner, DateTimeOffset now)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO provider_accounts (Id,OwnerUserId,ProviderId,DisplayName,Enabled,CreatedAt,UpdatedAt,Revision)
            VALUES ({id},{owner},'spotify','Saved account',1,{now.UtcTicks},{now.UtcTicks},9);
            """);
    }

    private static async Task InsertPrivateLinkAsync(AllstarrDbContext db, Guid id, Guid owner, Guid account, DateTimeOffset now)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO playlist_links
                (Id,OwnerUserId,ProviderAccountId,SourceProviderId,SourcePlaylistId,SourcePlaylistIdHash,
                 TargetProtocol,TargetBackendInstanceId,TargetPlaylistId,DestinationMode,Mode,ProjectionMode,
                 MaterializationMode,ImportMode,TrackRetention,MirrorStaleEntries,PreserveManualEntries,
                 SyncName,SyncDescription,SyncArtwork,RuleVersion,PolicyVersion,CreatedAt,UpdatedAt,Revision)
            VALUES ({id},{owner},{account},'spotify','saved-source',{new string('l', 64)},
                    'jellyfin','fixture','saved-destination','Existing','Virtual','Resolved','Reconcile','Linked','OnDemand',
                    0,1,1,1,1,'1','1',{now.UtcTicks},{now.UtcTicks},7);
            """);
    }

    private static async Task InsertPublicLinkAsync(AllstarrDbContext db, Guid id, Guid owner, string provider, string hash, DateTimeOffset now)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO playlist_links
                (Id,OwnerUserId,ProviderAccountId,SourceProviderId,SourcePlaylistId,SourcePlaylistIdHash,
                 TargetProtocol,TargetBackendInstanceId,TargetPlaylistId,DestinationMode,Mode,ProjectionMode,
                 MaterializationMode,ImportMode,TrackRetention,MirrorStaleEntries,PreserveManualEntries,
                 SyncName,SyncDescription,SyncArtwork,RuleVersion,PolicyVersion,CreatedAt,UpdatedAt,Revision)
            VALUES ({id},{owner},NULL,{provider},'public-source',{hash},
                    'jellyfin','fixture','public-destination','Existing','Virtual','Resolved','Reconcile','Linked','OnDemand',
                    0,1,1,1,1,'1','1',{now.UtcTicks},{now.UtcTicks},1);
            """);
    }

    private static async Task InsertSnapshotAsync(
        AllstarrDbContext db,
        Guid id,
        Guid owner,
        Guid link,
        Guid? account,
        DateTimeOffset now,
        string name)
    {
        if (account is { } accountId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO playlist_source_snapshots
                    (Id,OwnerUserId,PlaylistLinkId,ProviderAccountId,SnapshotVersion,ProviderRevision,Name,
                     PayloadSha256,CorrelationId,RetrievedAt)
                VALUES ({id},{owner},{link},{accountId},1,'revision-1',{name},{new string('s', 64)},'correlation-1',{now.UtcTicks});
                """);
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO playlist_source_snapshots
                (Id,OwnerUserId,PlaylistLinkId,ProviderAccountId,SnapshotVersion,ProviderRevision,Name,
                 PayloadSha256,CorrelationId,RetrievedAt)
            VALUES ({id},{owner},{link},NULL,1,'revision-1',{name},{new string('s', 64)},'correlation-1',{now.UtcTicks});
            """);
    }

    private static async Task InsertExternalSnapshotAsync(
        AllstarrDbContext db,
        Guid id,
        Guid owner,
        Guid? account,
        DateTimeOffset now,
        string provider,
        string externalHash)
    {
        var emptyJson = "{}";
        if (account is { } accountId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO external_metadata_snapshots
                    (Id,OwnerUserId,ProviderAccountId,BackendInstanceId,BackendPrincipalId,Protocol,ProviderId,
                     ResourceKind,ExternalIdHash,SnapshotVersion,ProviderRevision,PayloadJson,PayloadSha256,
                     CorrelationId,RetrievedAt)
                VALUES ({id},{owner},{accountId},'fixture','listener','jellyfin',{provider},'track',{externalHash},1,
                        'revision-1',{emptyJson},{new string('e', 64)},'correlation-1',{now.UtcTicks});
                """);
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO external_metadata_snapshots
                (Id,OwnerUserId,ProviderAccountId,BackendInstanceId,BackendPrincipalId,Protocol,ProviderId,
                 ResourceKind,ExternalIdHash,SnapshotVersion,ProviderRevision,PayloadJson,PayloadSha256,
                 CorrelationId,RetrievedAt)
            VALUES ({id},{owner},NULL,'fixture','listener','jellyfin',{provider},'track',{externalHash},1,
                    'revision-1',{emptyJson},{new string('e', 64)},'correlation-1',{now.UtcTicks});
            """);
    }

    private static async Task<long> CountAsync(AllstarrDbContext db, string table) =>
        await ScalarAsync<long>(db, $"SELECT COUNT(*) AS Value FROM {table}");

    private static async Task<string[]> SnapshotLegacyRowsAsync(AllstarrDbContext db)
    {
        var snapshots = new List<string>();
        foreach (var table in new[] { "users", "provider_accounts", "playlist_links", "playlist_source_snapshots", "external_metadata_snapshots" })
        {
            foreach (var row in await ReadRowsAsync(db, $"SELECT * FROM {table}"))
                snapshots.Add($"{table}|{row}");
        }

        return snapshots.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> ReadRowsAsync(AllstarrDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var values = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => $"{reader.GetName(index)}={CanonicalValue(reader.GetValue(index))}")
                    .OrderBy(value => value, StringComparer.Ordinal);
                rows.Add(string.Join(",", values));
            }

            return rows.OrderBy(row => row, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
    }

    private static string CanonicalValue(object value) => value switch
    {
        DBNull => "<null>",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static async Task<T> ScalarAsync<T>(AllstarrDbContext db, string sql) =>
        await db.Database.SqlQueryRaw<T>(sql).SingleAsync();
}
