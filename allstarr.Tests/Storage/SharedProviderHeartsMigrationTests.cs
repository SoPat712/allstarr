using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace allstarr.Tests;

[Trait("Lane", "ReleaseCritical")]
public sealed class SharedProviderHeartsMigrationTests
{
    private const string Before = "20261025040000_ProblemReports";
    private static readonly string[] AddedObjects =
    [
        "index|IX_favorite_event_link_baseline", "index|IX_favorite_event_link_item", "index|IX_favorite_events_OwnerUserId_SharedLinkId",
        "index|IX_linked_provider_favorite_states_LastIntentEventId", "index|IX_provider_favorite_link_member_role",
        "index|IX_provider_favorite_link_requests_JobId", "index|IX_provider_favorite_link_requests_LinkId",
        "index|IX_provider_favorite_link_created", "index|IX_provider_favorite_link_pair",
        "index|IX_provider_favorite_links_ConfirmedByUserId", "index|IX_provider_favorite_links_UnionJobId",
        "table|linked_provider_favorite_states", "table|provider_favorite_link_members",
        "table|provider_favorite_link_requests", "table|provider_favorite_links",
        "trigger|TR_favorite_event_scope_immutable", "trigger|TR_provider_favorite_link_member_immutable"
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedUpgradeUsesTheRealParserAndLeavesNoFunctionDependency(bool synchronous)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        var owner = Guid.NewGuid();
        var synthetic = Guid.NewGuid();
        db.Users.AddRange(User(owner, false), User(synthetic, true));
        var jobs = new Dictionary<Guid, Guid>();
        foreach (var user in new[] { owner, synthetic })
        {
            var job = new DurableJobRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user,
                ScopeKey = "fixture",
                Type = FavoriteActionPipeline.JobType,
                IdempotencyKey = user.ToString("N"),
                State = DurableJobState.Pending
            };
            jobs[user] = job.Id;
            db.Jobs.Add(job);
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE VIEW favorite_fixture_view AS SELECT "Id" FROM favorite_events;
            CREATE VIEW favorite_fixture_dependent AS SELECT "Id" FROM favorite_fixture_view;
            CREATE TRIGGER favorite_fixture_event AFTER UPDATE OF "State" ON favorite_events BEGIN SELECT count(*) FROM favorite_fixture_dependent; END;
            CREATE TRIGGER favorite_fixture_external AFTER INSERT ON users BEGIN SELECT count(*) FROM favorite_events; END;
            """);
        var valid = await Seed(db, owner, jobs[owner], "ext-deezer-song-42", true, false);
        var invalid = new List<Guid>
        {
            await Seed(db, owner, jobs[owner], "native-track", true, false),
            await Seed(db, owner, jobs[owner], "ext-bad--provider-song-42", true, false),
            await Seed(db, owner, jobs[owner], "ext-deezer-song-orphan", false, false),
            await Seed(db, owner, jobs[owner], "ext-deezer-song-stale", true, true),
            await Seed(db, synthetic, jobs[synthetic], "ext-deezer-song-test", true, false)
        };
        var before = await Objects(db);
        var foreignKeys = await ForeignKeys(db);
        var columns = await FavoriteColumns(db);
        var uniqueKeys = await UniqueKeys(db);
        Assert.DoesNotContain("CHECK", before["table|favorite_events"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COLLATE", before["table|favorite_events"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FK_favorite_event_job_lineage", before["table|favorite_events"]);
        var oldRows = await OriginalEvents(db);

        if (synchronous) migrator.Migrate(); else await migrator.MigrateAsync();

        var after = await Objects(db);
        Assert.Equal(AddedObjects.Order(), after.Keys.Except(before.Keys).Order());
        foreach (var item in before.Where(item => item.Key != "table|favorite_events")) Assert.Equal(item.Value, after[item.Key]);
        Assert.Contains("CK_favorite_event_shared_scope", after["table|favorite_events"]);
        Assert.Contains("\"SharedLifecycleVersion\" IS NOT NULL", after["table|favorite_events"]);
        Assert.Contains("FK_favorite_events_provider_favorite_link_members", after["table|favorite_events"]);
        Assert.Contains("FK_favorite_event_job_lineage", after["table|favorite_events"]);
        Assert.Equal(oldRows, await OriginalEvents(db));
        var upgradedForeignKeys = await ForeignKeys(db);
        Assert.All(foreignKeys, row => Assert.Contains(row, upgradedForeignKeys));
        var upgradedColumns = await FavoriteColumns(db);
        Assert.Equal(columns, upgradedColumns.Where(row => !row.StartsWith("IsRealProviderIntent|", StringComparison.Ordinal) &&
            !row.StartsWith("SharedLifecycleVersion|", StringComparison.Ordinal) && !row.StartsWith("SharedLinkId|", StringComparison.Ordinal)));
        Assert.Equal(columns.Length + 3, upgradedColumns.Length);
        Assert.Equal(uniqueKeys, await UniqueKeys(db));
        var flags = await db.FavoriteEvents.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.IsRealProviderIntent);
        Assert.True(flags[valid]);
        Assert.All(invalid, id => Assert.False(flags[id]));
        Assert.DoesNotContain(after.Values, sql => sql.Contains("allstarr_valid_favorite_resource", StringComparison.Ordinal));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.SqlQueryRaw<int>("SELECT allstarr_valid_favorite_resource('ext-deezer-song-42') AS Value").SingleAsync());
        await db.Database.CloseConnectionAsync();
        await db.Database.OpenConnectionAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.SqlQueryRaw<int>("SELECT allstarr_valid_favorite_resource('ext-deezer-song-42') AS Value").SingleAsync());
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        await migrator.MigrateAsync(Before);
        var reverted = await Objects(db);
        Assert.Equal(before.Keys.Order(), reverted.Keys.Order());
        foreach (var item in before.Where(item => item.Key != "table|favorite_events")) Assert.Equal(item.Value, reverted[item.Key]);
        Assert.Equal(columns, await FavoriteColumns(db));
        Assert.Equal(foreignKeys, await ForeignKeys(db));
        Assert.Equal(uniqueKeys, await UniqueKeys(db));
        Assert.Equal(oldRows, await OriginalEvents(db));
    }

    [Fact]
    public async Task PopulatedDowngradeRefusesBeforeMutationAndNullLifecycleIsRejectedBySql()
    {
        await using var f = await FavoriteLinkTests.Fixture.CreateAsync();
        var link = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        var heart = await f.Heart(f.Jellyfin, true, "scope");
        await using var db = f.Factory.CreateDbContext();
        var before = await Objects(db);
        var events = await OriginalEvents(db);
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(Before));
        Assert.Contains("CK_provider_favorite_links_require_restore", error.Message);
        Assert.Equal(before, await Objects(db));
        Assert.Equal(events, await OriginalEvents(db));
        Assert.Equal(link.LinkId, (await db.Set<ProviderFavoriteLinkRecord>().SingleAsync()).Id);
        var invalidId = Guid.NewGuid();
        var invalid = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO favorite_events ("Id","OwnerUserId","Protocol","BackendInstanceId","BackendPrincipalId","ItemId","Operation","SourceRevision","EventKey","CorrelationId","PolicySnapshotJson","JobId","State","CreatedAt","UpdatedAt","Revision","IsRealProviderIntent","SharedLinkId","SharedLifecycleVersion")
            SELECT {invalidId},"OwnerUserId","Protocol","BackendInstanceId","BackendPrincipalId","ItemId","Operation","SourceRevision",{invalidId.ToString("N")},"CorrelationId","PolicySnapshotJson","JobId","State","CreatedAt","UpdatedAt","Revision",1,"SharedLinkId",NULL FROM favorite_events WHERE "Id"={heart.EventId}
            """));
        Assert.Contains("CK_favorite_event_shared_scope", invalid.Message);
        Assert.Equal(0, (await db.FavoriteEvents.AsNoTracking().SingleAsync()).SharedLifecycleVersion);
    }

    [Theory]
    [InlineData("real")]
    [InlineData("link")]
    [InlineData("generation")]
    [InlineData("member-principal")]
    [InlineData("member-protocol")]
    [InlineData("member-instance")]
    [InlineData("member-fingerprint")]
    [InlineData("member-cutoff")]
    [InlineData("member-cutoff-id")]
    public async Task AcceptedScopeAndFrozenMembershipAreImmutable(string field)
    {
        await using var f = await FavoriteLinkTests.Fixture.CreateAsync();
        await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        await f.Heart(f.Jellyfin, true, "scope");
        await using var db = f.Factory.CreateDbContext();
        var item = await db.FavoriteEvents.SingleAsync();
        var member = await db.Set<ProviderFavoriteLinkMemberRecord>().SingleAsync(row => row.UserId == f.Jellyfin.Id);
        if (field == "real") item.IsRealProviderIntent = false;
        if (field == "link") item.SharedLinkId = null;
        if (field == "generation") item.SharedLifecycleVersion++;
        if (field == "member-principal") member.BackendPrincipalId = "replacement";
        if (field == "member-protocol") member.Protocol = "subsonic";
        if (field == "member-instance") member.BackendInstanceId = "replacement";
        if (field == "member-fingerprint") member.IdentityFingerprint = new string('a', 64);
        if (field == "member-cutoff") member.UpperAt = f.Clock.UtcNow;
        if (field == "member-cutoff-id") member.UpperId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("IsRealProviderIntent", "0")]
    [InlineData("SharedLinkId", "NULL")]
    [InlineData("SharedLifecycleVersion", "99")]
    [InlineData("BackendPrincipalId", "'replacement'")]
    [InlineData("UpperAt", "1")]
    public async Task SqlCannotRewriteAcceptedScopeOrFrozenBinding(string field, string value)
    {
        await using var f = await FavoriteLinkTests.Fixture.CreateAsync();
        await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        await f.Heart(f.Jellyfin, true, "immutable");
        await using var db = f.Factory.CreateDbContext();
        var table = field is "BackendPrincipalId" or "UpperAt" ? "provider_favorite_link_members" : "favorite_events";
        var sql = field switch
        {
            "IsRealProviderIntent" => "UPDATE favorite_events SET \"IsRealProviderIntent\"=0",
            "SharedLinkId" => "UPDATE favorite_events SET \"SharedLinkId\"=NULL",
            "SharedLifecycleVersion" => "UPDATE favorite_events SET \"SharedLifecycleVersion\"=99",
            "BackendPrincipalId" => "UPDATE provider_favorite_link_members SET \"BackendPrincipalId\"='replacement'",
            "UpperAt" => "UPDATE provider_favorite_link_members SET \"UpperAt\"=1",
            _ => throw new ArgumentException("Unknown fixture field.", nameof(field))
        };
        Assert.NotEmpty(value);
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Contains(table == "favorite_events" ? "CK_favorite_event_scope_immutable" : "CK_provider_favorite_link_member_immutable", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRebuildRollsBackEveryGuardAndCanMigrateAfterReopening(bool synchronous)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(false);
        var failure = new DdlFailure();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(failure).Options;
        await using var db = new AllstarrDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        var before = await Objects(db);
        failure.Enabled = true;
        if (synchronous) Assert.Throws<InvalidOperationException>(() => migrator.Migrate());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync());
        failure.Enabled = false;
        Assert.Equal(before, await Objects(db));
        await db.Database.CloseConnectionAsync();
        await db.Database.OpenConnectionAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.SqlQueryRaw<int>("SELECT allstarr_valid_favorite_resource('ext-deezer-song-42') AS Value").SingleAsync());
        await migrator.MigrateAsync();
        var after = await Objects(db);
        Assert.All(before.Where(item => item.Key != "table|favorite_events"), item => Assert.Equal(item.Value, after[item.Key]));
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync());
    }

    [Fact]
    public async Task LatestLegacyRawItemLookupSeeksItsExactScopeAndItemIndex()
    {
        await using var f = await FavoriteLinkTests.Fixture.CreateAsync();
        await f.Heart(f.Jellyfin, true, "index-proof");
        var link = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        await using var db = f.Factory.CreateDbContext();
        var member = await db.Set<ProviderFavoriteLinkMemberRecord>().SingleAsync(item => item.LinkId == link.LinkId && item.UserId == f.Jellyfin.Id);
        var latest = FavoriteLinkScope.Legacy(db, member).Where(item => item.ItemId == "ext-deezer-song-42").OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(1);
        var sql = latest.ToQueryString();
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        var start = sql.IndexOf("SELECT", StringComparison.Ordinal);
        command.CommandText = "EXPLAIN QUERY PLAN " + sql[start..];
        // EXPLAIN accepts unbound parameters; its access path depends on predicates, not fixture values.
        var parameters = System.Text.RegularExpressions.Regex.Matches(sql[start..], "@[A-Za-z0-9_]+").Select(match => match.Value).Distinct();
        foreach (var name in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = DBNull.Value;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Assert.Contains(details, detail => detail.Contains("SEARCH", StringComparison.Ordinal) && detail.Contains("IX_favorite_event_link_item", StringComparison.Ordinal) && detail.Contains("ItemId=?", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.Contains("SCAN favorite_events", StringComparison.Ordinal));
    }

    private static UserRecord User(Guid id, bool synthetic) => new()
    {
        Id = id,
        BackendType = "jellyfin",
        BackendInstanceId = "home",
        BackendPrincipalId = id.ToString("N"),
        DisplayName = "Listener",
        Enabled = true,
        IsSynthetic = synthetic
    };
    private static async Task<Guid> Seed(AllstarrDbContext db, Guid owner, Guid job, string item, bool state, bool stale)
    {
        var id = Guid.NewGuid();
        var stateId = Guid.NewGuid();
        var principal = stale ? "stale" : owner.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO favorite_events ("Id","OwnerUserId","Protocol","BackendInstanceId","BackendPrincipalId","ItemId","Operation","SourceRevision","EventKey","CorrelationId","PolicySnapshotJson","JobId","State","CreatedAt","UpdatedAt","Revision")
            VALUES ({id},{owner},'jellyfin','home',{principal},{item},'Favorite','1',{id.ToString("N")},'fixture',json_object(),{job},'Pending',1,1,1)
            """);
        if (state) await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO favorite_states ("Id","OwnerUserId","Protocol","BackendInstanceId","ItemId","IsFavorite","LastEventId","UpdatedAt","Revision")
            VALUES ({stateId},{owner},'jellyfin','home',{item},1,{id},1,1)
            """);
        return id;
    }
    private static async Task<string[]> OriginalEvents(AllstarrDbContext db) => await db.Database.SqlQueryRaw<string>("""
        SELECT "Id"||'|'||"OwnerUserId"||'|'||"Protocol"||'|'||"BackendInstanceId"||'|'||"BackendPrincipalId"||'|'||"ItemId"||'|'||"Operation"||'|'||"SourceRevision"||'|'||"EventKey"||'|'||"CorrelationId"||'|'||"PolicySnapshotJson"||'|'||"JobId"||'|'||"State"||'|'||"CreatedAt"||'|'||"UpdatedAt"||'|'||"Revision" AS Value FROM favorite_events ORDER BY "Id"
        """).ToArrayAsync();
    private static async Task<Dictionary<string, string>> Objects(AllstarrDbContext db)
    {
        var rows = await db.Database.SqlQueryRaw<string>("SELECT type||'|'||name||'|'||sql AS Value FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type,name").ToArrayAsync();
        return rows.ToDictionary(row => row[..row.IndexOf('|', row.IndexOf('|') + 1)], row => row[(row.IndexOf('|', row.IndexOf('|') + 1) + 1)..]);
    }
    private static Task<string[]> ForeignKeys(AllstarrDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT "table"||'|'||on_update||'|'||on_delete||'|'||match||'|'||group_concat("from"||'>'||"to",',') AS Value
        FROM (SELECT * FROM pragma_foreign_key_list('favorite_events') ORDER BY id,seq) GROUP BY id ORDER BY Value
        """).ToArrayAsync();
    private static Task<string[]> FavoriteColumns(AllstarrDbContext db) => db.Database.SqlQueryRaw<string>("SELECT name||'|'||type||'|'||\"notnull\"||'|'||coalesce(dflt_value,'<null>')||'|'||pk AS Value FROM pragma_table_info('favorite_events') ORDER BY name").ToArrayAsync();
    private static Task<string[]> UniqueKeys(AllstarrDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT origin||'|'||partial||'|'||(SELECT group_concat(name||':'||coll||':'||desc,',') FROM
          (SELECT * FROM pragma_index_xinfo(i.name) WHERE key=1 ORDER BY seqno)) AS Value
        FROM pragma_index_list('favorite_events') i WHERE "unique"=1 ORDER BY Value
        """).ToArrayAsync();
    private sealed class DdlFailure : DbCommandInterceptor
    {
        internal bool Enabled { get; set; }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Fail(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Fail(command); return ValueTask.FromResult(result); }
        private void Fail(DbCommand command)
        { if (Enabled && command.CommandText.Contains("INSERT INTO \"ef_temp_favorite_events\"", StringComparison.Ordinal)) throw new InvalidOperationException("Interrupted fixture rebuild."); }
    }
}
