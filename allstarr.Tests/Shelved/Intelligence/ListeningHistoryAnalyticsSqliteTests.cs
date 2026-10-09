using allstarr.Core.Identity;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace allstarr.Tests;

public sealed class ListeningHistoryAnalyticsSqliteTests
{
    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ScopedPeriodAndCursorQueries_UseHistoryIndexAtTenThousandRows()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var user = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var nowTicks = now.UtcTicks;

        db.Users.Add(new UserRecord
        {
            Id = user,
            DisplayName = "Listener",
            Enabled = true,
            BackendType = "jellyfin",
            BackendInstanceId = "main",
            BackendPrincipalId = "listener",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            WITH RECURSIVE sequence(series) AS (SELECT 1 UNION ALL SELECT series + 1 FROM sequence WHERE series < 10000)
            INSERT INTO listening_events
                ("Id", "OwnerUserId", "Protocol", "BackendInstanceId",
                 "OccurrenceKey", "State", "ListenedAt", "UpdatedAt", "DurationMilliseconds",
                 "SourceKind", "TrackReference", "Title", "Artist", "Album", "MusicBrainzEnrichmentState", "ChosenByUser", "Revision")
            SELECT printf('%08X-0000-0000-0000-000000000000', series), {{user}}, 'jellyfin',
                   CASE WHEN series % 10 = 0 THEN 'main' ELSE 'decoy' END,
                   printf('%064x', series), 'Completed',
                   {{nowTicks}} - (series % 365) * {{TimeSpan.TicksPerDay}},
                   {{nowTicks}}, 180000, 'protocol', 'track-' || series,
                   'Track ' || (series % 100), 'Artist ' || (series % 25),
                   'Album ' || (series % 10), 'NotRequested', 0, 1
            FROM sequence;
            ANALYZE listening_events;
            """);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        var from = now.AddDays(-30).UtcTicks;
        var to = now.AddMilliseconds(1).UtcTicks;
        var cursor = now.AddDays(-15).UtcTicks;
        var cursorId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var pagePlan = await ExplainAsync(connection, """
            SELECT "Id", "ListenedAt"
            FROM listening_events
            WHERE "OwnerUserId" = @user
              AND "Protocol" = 'jellyfin' AND "BackendInstanceId" = 'main'
              AND "State" = 'Completed'
              AND "ListenedAt" >= @from AND "ListenedAt" < @to
              AND ("ListenedAt" < @cursor OR ("ListenedAt" = @cursor AND "Id" < @cursor_id))
            ORDER BY "ListenedAt" DESC, "Id" DESC
            LIMIT 101
            """, user, from, to, cursor, cursorId);
        var topPlan = await ExplainAsync(connection, """
            SELECT "Artist", count(*)
            FROM listening_events
            WHERE "OwnerUserId" = @user
              AND "Protocol" = 'jellyfin' AND "BackendInstanceId" = 'main'
              AND "State" = 'Completed'
              AND "ListenedAt" >= @from AND "ListenedAt" < @to
            GROUP BY "Artist"
            ORDER BY count(*) DESC
            LIMIT 10
            """, user, from, to);
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            SELECT sql FROM sqlite_master
            WHERE type = 'index' AND name = 'IX_listening_event_scope_history'
            """;
        var indexDefinition = Assert.IsType<string>(await indexCommand.ExecuteScalarAsync());

        Assert.Contains("IX_listening_event_scope_history", pagePlan, StringComparison.Ordinal);
        Assert.Contains("IX_listening_event_scope_history", topPlan, StringComparison.Ordinal);
        Assert.Contains("\"State\", \"ListenedAt\", \"Id\"", indexDefinition, StringComparison.Ordinal);
    }

    private static async Task<string> ExplainAsync(
        SqliteConnection connection,
        string query,
        Guid user,
        long from,
        long to,
        long? cursor = null,
        Guid? cursorId = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + query;
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        if (cursor.HasValue && cursorId.HasValue)
        {
            command.Parameters.AddWithValue("cursor", cursor.Value);
            command.Parameters.AddWithValue("cursor_id", cursorId.Value);
        }
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        command.CommandText = query;
        var elapsed = Stopwatch.StartNew();
        var rows = 0;
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) rows++;
        Assert.InRange(rows, 1, 101);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2),
            $"Indexed history query took {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
        return string.Join('\n', plan);
    }
}
