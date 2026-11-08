using allstarr.Controllers;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Core.Storage.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class ListenerActivityIndexMigrationTests
{
    [Fact]
    public async Task ListenerLookupsReadOnlyTheNewestListensThroughTheIndex()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261107120000_HomeNotableActivity");
        var owner = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        db.Users.Add(new UserRecord
        {
            Id = owner,
            DisplayName = "Listener",
            Enabled = true,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = owner.ToString(),
            CreatedAt = at,
            UpdatedAt = at
        });
        db.ListeningEvents.Add(new ListeningEventRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            OccurrenceKey = "kept",
            State = ListeningEventState.Completed,
            StartedAt = at.AddMinutes(-4),
            ListenedAt = at,
            UpdatedAt = at,
            SourceKind = "protocol",
            TrackReference = "Kept",
            Title = "Kept",
            Artist = "Stored artist"
        });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();

        Assert.Equal(1, await db.ListeningEvents.CountAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            $"SELECT count(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = '{ListenerActivityIndex.IndexName}'").SingleAsync());
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var activity = Assert.Single(await db.Database.SqlQueryRaw<ProviderAccountsController.ListenerActivityRow>(
            ProviderAccountsController.ListenerActivitySql, now.UtcTicks, now.AddDays(-7).UtcTicks).ToListAsync());
        Assert.Equal((owner, at.UtcTicks, 1), (activity.UserId, activity.LastPlayedTicks!.Value, activity.PlaysThisWeek));
        Assert.Equal("Kept", Assert.Single(await db.Database.SqlQueryRaw<ProviderAccountsController.ListenerPlayRow>(
            ProviderAccountsController.RecentPlaysSql, now.UtcTicks).ToListAsync()).Title);

        await db.Database.OpenConnectionAsync();
        foreach (var sql in new[] { ProviderAccountsController.ListenerActivitySql, ProviderAccountsController.RecentPlaysSql })
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql.Replace("{0}", "@now", StringComparison.Ordinal)
                .Replace("{1}", "@since", StringComparison.Ordinal);
            foreach (var (name, value) in new[] { ("@now", now.UtcTicks), ("@since", now.AddDays(-7).UtcTicks) })
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            await using var reader = await command.ExecuteReaderAsync();
            var plan = new List<string>();
            while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
            Assert.Contains(plan, step => step.Contains(ListenerActivityIndex.IndexName, StringComparison.Ordinal));
            Assert.DoesNotContain(plan, step => step.StartsWith("SCAN e", StringComparison.Ordinal));
        }
    }
}
