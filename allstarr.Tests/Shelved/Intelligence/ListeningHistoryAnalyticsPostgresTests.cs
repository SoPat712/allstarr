using allstarr.Core.Identity;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace allstarr.Tests;

public sealed class ListeningHistoryAnalyticsPostgresTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ScopedPeriodAndCursorQueries_UseHistoryIndexAtTenThousandRows()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var nowTicks = now.UtcTicks;

        db.Tenants.Add(new TenantRecord
        {
            Id = tenant,
            Slug = "analytics-plan",
            Name = "Analytics plan",
            CreatedAt = now
        });
        db.Users.Add(new PlatformUserRecord
        {
            Id = user,
            TenantId = tenant,
            DisplayName = "Listener",
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO listening_events
                ("Id", "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId",
                 "OccurrenceKey", "State", "ListenedAt", "UpdatedAt", "DurationMilliseconds",
                 "SourceKind", "TrackReference", "Title", "Artist", "Album")
            SELECT md5(series::text)::uuid, {{tenant}}, {{user}}, 'jellyfin',
                   CASE WHEN series % 10 = 0 THEN 'main' ELSE 'decoy' END, 'music',
                   md5('occurrence-' || series::text), 'Completed',
                   {{nowTicks}} - (series % 365) * {{TimeSpan.TicksPerDay}}::bigint,
                   {{nowTicks}}, 180000, 'protocol', 'track-' || series::text,
                   'Track ' || (series % 100)::text, 'Artist ' || (series % 25)::text,
                   'Album ' || (series % 10)::text
            FROM generate_series(1, 10000) AS series;
            ANALYZE listening_events;
            """);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var from = now.AddDays(-30).UtcTicks;
        var to = now.AddMilliseconds(1).UtcTicks;
        var cursor = now.AddDays(-15).UtcTicks;
        var cursorId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var pagePlan = await ExplainAsync(connection, """
            SELECT "Id", "ListenedAt"
            FROM listening_events
            WHERE "TenantId" = @tenant AND "OwnerUserId" = @user
              AND "Protocol" = 'jellyfin' AND "BackendInstanceId" = 'main'
              AND "LibraryScopeId" = 'music' AND "State" = 'Completed'
              AND "ListenedAt" >= @from AND "ListenedAt" < @to
              AND ("ListenedAt" < @cursor OR ("ListenedAt" = @cursor AND "Id" < @cursor_id))
            ORDER BY "ListenedAt" DESC, "Id" DESC
            LIMIT 101
            """, tenant, user, from, to, cursor, cursorId);
        var topPlan = await ExplainAsync(connection, """
            SELECT "Artist", count(*)
            FROM listening_events
            WHERE "TenantId" = @tenant AND "OwnerUserId" = @user
              AND "Protocol" = 'jellyfin' AND "BackendInstanceId" = 'main'
              AND "LibraryScopeId" = 'music' AND "State" = 'Completed'
              AND "ListenedAt" >= @from AND "ListenedAt" < @to
            GROUP BY "Artist"
            ORDER BY count(*) DESC
            LIMIT 10
            """, tenant, user, from, to);
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'public' AND indexname = 'IX_listening_event_scope_history'
            """;
        var indexDefinition = Assert.IsType<string>(await indexCommand.ExecuteScalarAsync());

        Assert.Contains("IX_listening_event_scope_history", pagePlan, StringComparison.Ordinal);
        Assert.Contains("IX_listening_event_scope_history", topPlan, StringComparison.Ordinal);
        Assert.Contains("\"State\", \"ListenedAt\", \"Id\"", indexDefinition, StringComparison.Ordinal);
        Assert.Contains("actual time", pagePlan, StringComparison.Ordinal);
        Assert.Contains("actual time", topPlan, StringComparison.Ordinal);
    }

    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        string query,
        Guid tenant,
        Guid user,
        long from,
        long to,
        long? cursor = null,
        Guid? cursorId = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + query;
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        if (cursor.HasValue && cursorId.HasValue)
        {
            command.Parameters.AddWithValue("cursor", cursor.Value);
            command.Parameters.AddWithValue("cursor_id", cursorId.Value);
        }
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
        return string.Join('\n', plan);
    }
}
