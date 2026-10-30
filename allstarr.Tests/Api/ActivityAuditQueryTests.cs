using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ActivityAuditQueryTests
{
    [Fact]
    public async Task HistoryAuditLookupUsesTheJobIdentityIndex()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = Guid.CreateVersion7();
        var query = ActivityJobPresentation.HistoryAudits(db)
            .Where(item => item.ActorUserId == owner)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(40);
        await db.Database.OpenConnectionAsync();
        await using var command = query.CreateDbCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, detail => detail.Contains("SEARCH j USING", StringComparison.Ordinal) &&
            detail.Contains("(Id=?", StringComparison.Ordinal));
    }
}
