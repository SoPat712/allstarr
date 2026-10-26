using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ProviderCheckHistoryTests
{
    [Fact]
    public async Task ReadRecentUsesCurrentSubjectsAndRevisionBoundsWithoutWriting()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
        var history = new ProviderCheckHistory(factory, clock);
        var accountId = Guid.CreateVersion7();
        var key = ProviderRuntimeStatusKey.CreateManaged("deezer", ProviderCapabilities.Streaming, accountId);
        var publicKey = ProviderRuntimeStatusKey.CreateAccountFree("deezer", ProviderCapabilities.Metadata);
        var target = new ProviderCheckHistoryTarget(key, 7, "Fixture account", "shared");
        var publicTarget = new ProviderCheckHistoryTarget(publicKey, null, "Public", "public");
        var now = clock.GetUtcNow();

        await using (var db = factory.CreateDbContext())
        {
            db.AuditEvents.AddRange(
                Event(key, 7, Status(key, ProviderHealthState.Degraded, now.AddMinutes(-1), "timeout"), now.AddMinutes(-1)),
                Event(key, 7, Status(key, ProviderHealthState.Unknown, now.AddMinutes(-2), "sample-unavailable"), now.AddMinutes(-2)),
                Event(publicKey, null, Status(publicKey, ProviderHealthState.Healthy, now.AddMinutes(-2), null), now.AddMinutes(-2)),
                Event(key, 8, Status(key, ProviderHealthState.Healthy, now.AddMinutes(-3), null), now.AddMinutes(-3)),
                Event(ProviderRuntimeStatusKey.CreateManaged("deezer", ProviderCapabilities.Streaming, Guid.CreateVersion7()), 7,
                    Status(key, ProviderHealthState.Healthy, now.AddMinutes(-4), null), now.AddMinutes(-4)),
                Event(publicKey, null, Status(publicKey, ProviderHealthState.Healthy, now.AddDays(1), null), now.AddDays(1)),
                Event(publicKey, null, Status(publicKey, ProviderHealthState.Healthy, now.AddDays(-31), null), now.AddDays(-31)),
                Event(publicKey, null, Status(publicKey, ProviderHealthState.Healthy, now.AddMinutes(1), null), now.AddMinutes(-1)),
                Event(publicKey, null, Status(key, ProviderHealthState.Healthy, now.AddMinutes(-1), null), now.AddMinutes(-1)),
                new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    Category = ProviderCheckHistory.Category,
                    SubjectType = ProviderCheckHistory.Category,
                    SubjectId = target.SubjectId,
                    DetailsJson = "{malformed",
                    CreatedAt = now.AddMinutes(-5)
                });
            await db.SaveChangesAsync();
        }

        await using (var db = factory.CreateDbContext())
        {
            var before = await db.AuditEvents.CountAsync();
            var rows = await history.ReadRecentAsync([target, publicTarget], 24, default);
            var after = await db.AuditEvents.CountAsync();

            Assert.Equal(3, rows.Count);
            Assert.Contains(rows, row => row.Result.Key == key && row.Result.Revision == 7);
            Assert.Contains(rows, row => row.Result.Key == publicKey && row.Result.Revision == null);
            Assert.Equal(before, after);
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(24, 24)]
    [InlineData(100, 24)]
    public async Task ReadRecentBoundsAndOrdersReturnedRows(int requested, int expected)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
        var history = new ProviderCheckHistory(factory, new ManualTimeProvider(now));
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", ProviderCapabilities.Metadata);
        await using (var db = factory.CreateDbContext())
        {
            db.AuditEvents.AddRange(Enumerable.Range(1, 35).Select(index =>
                Event(key, null, Status(key, ProviderHealthState.Healthy, now.AddSeconds(-index), null), now.AddSeconds(-index))));
            await db.SaveChangesAsync();
        }

        var rows = await history.ReadRecentAsync([new(key, null, "Public", "public")], requested, default);

        Assert.Equal(expected, rows.Count);
        Assert.Equal(Enumerable.Range(1, expected).Select(index => now.AddSeconds(-index)),
            rows.Select(row => row.Result.Status.TestedAt!.Value));
        await using var check = factory.CreateDbContext();
        Assert.Equal(35, await check.AuditEvents.CountAsync());
    }

    [Theory]
    [InlineData("RateLimited", "rate-limited")]
    [InlineData("sample-unavailable", "sample-unavailable")]
    [InlineData("timeout", "timeout")]
    [InlineData("https://source.test/?token=private", "probe_failed")]
    public void ReasonProjectionOnlyReturnsSafeCodes(string reason, string expected) =>
        Assert.Equal(expected, ProviderCheckHistory.SafeReasonCode(reason));

    private static AuditEventRecord Event(
        ProviderRuntimeStatusKey key,
        long? revision,
        ProviderRuntimeStatus status,
        DateTimeOffset createdAt) => new()
        {
            Id = Guid.CreateVersion7(),
            Category = ProviderCheckHistory.Category,
            Action = "check-completed",
            Outcome = "recorded",
            SubjectType = ProviderCheckHistory.Category,
            SubjectId = $"{key.Provider}/{key.Capability}/{key.ProviderAccountId?.ToString("N") ?? "public"}",
            DetailsJson = JsonSerializer.Serialize(new ProviderCheckResult(key, revision, status)),
            CreatedAt = createdAt
        };

    private static ProviderRuntimeStatus Status(
        ProviderRuntimeStatusKey key,
        ProviderHealthState health,
        DateTimeOffset testedAt,
        string? reason) => new()
        {
            Provider = key.Provider,
            Capability = key.Capability,
            IsSupported = true,
            IsEnabled = true,
            Configuration = ProviderConfigurationState.Configured,
            Health = health,
            TestedAt = testedAt,
            ReasonCode = reason
        };
}
