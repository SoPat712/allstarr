using System.Reflection;
using allstarr.Services.Common;

namespace allstarr.Tests;

public class FavoritesMigrationServiceTests
{
    [Fact]
    public void ParsePendingDeletions_ParsesLegacyDictionaryFormat()
    {
        var scheduledDeletion = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        var parsed = ParsePendingDeletions($$"""
            {
              "ext-deezer-123": "{{scheduledDeletion:O}}"
            }
            """);

        Assert.Single(parsed);
        Assert.Equal(scheduledDeletion, parsed["ext-deezer-123"]);
    }

    [Fact]
    public void ParsePendingDeletions_ParsesSetFormatUsingFallbackDate()
    {
        var fallbackDeleteAtUtc = new DateTime(2026, 3, 23, 12, 0, 0, DateTimeKind.Utc);
        var parsed = ParsePendingDeletions("""
            [
              "ext-deezer-123",
              "ext-qobuz-456"
            ]
            """, fallbackDeleteAtUtc);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(fallbackDeleteAtUtc, parsed["ext-deezer-123"]);
        Assert.Equal(fallbackDeleteAtUtc, parsed["ext-qobuz-456"]);
    }

    [Fact]
    public void ParsePendingDeletions_ThrowsForUnsupportedFormat()
    {
        var method = typeof(FavoritesMigrationService).GetMethod(
            "ParsePendingDeletions",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var ex = Assert.Throws<TargetInvocationException>(() =>
            method!.Invoke(null, new object?[] { """{"bad":42}""", DateTime.UtcNow }));

        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException);
    }

    private static Dictionary<string, DateTime> ParsePendingDeletions(string json, DateTime? fallbackDeleteAtUtc = null)
    {
        var method = typeof(FavoritesMigrationService).GetMethod(
            "ParsePendingDeletions",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[]
        {
            json,
            fallbackDeleteAtUtc ?? new DateTime(2026, 3, 23, 0, 0, 0, DateTimeKind.Utc)
        });

        return Assert.IsType<Dictionary<string, DateTime>>(result);
    }
}
