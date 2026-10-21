using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Protocols.Subsonic;

public sealed record SubsonicQueueEntry(string Id, Song? Song, string? NativeJson, string? NativeXml);

public sealed record SubsonicSavedQueue(
    SubsonicQueueEntry[] Entries,
    int? CurrentIndex,
    long Position,
    string Username,
    DateTimeOffset Changed,
    string ChangedBy);

public sealed class SubsonicPlayQueueStore(IDbContextFactory<AllstarrDbContext> contexts)
{
    public async Task<SubsonicSavedQueue?> ReadAsync(ProtocolExecutionContext context, CancellationToken token)
    {
        RequireSubsonic(context);
        await using var db = await contexts.CreateDbContextAsync(token);
        var payload = await db.SubsonicPlayQueues.AsNoTracking()
            .Where(item => item.BackendInstanceId == context.BackendInstanceId &&
                item.BackendPrincipalId == context.VerifiedBackendPrincipalId)
            .Select(item => item.PayloadJson).SingleOrDefaultAsync(token);
        return payload == null ? null : JsonSerializer.Deserialize<SubsonicSavedQueue>(payload);
    }

    public async Task SaveAsync(ProtocolExecutionContext context, SubsonicSavedQueue queue, CancellationToken token)
    {
        RequireSubsonic(context);
        var payload = JsonSerializer.Serialize(queue);
        await using var db = await contexts.CreateDbContextAsync(token);
        // One atomic replacement keeps entries, occurrence and position from different saves together.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO subsonic_play_queues (BackendInstanceId, BackendPrincipalId, PayloadJson)
            VALUES ({context.BackendInstanceId}, {context.VerifiedBackendPrincipalId}, {payload})
            ON CONFLICT (BackendInstanceId, BackendPrincipalId)
            DO UPDATE SET PayloadJson = excluded.PayloadJson
            """, token);
    }

    private static void RequireSubsonic(ProtocolExecutionContext context)
    {
        if (context.Protocol != ProtocolKind.Subsonic)
            throw new InvalidOperationException("A verified Subsonic viewer is required.");
    }
}
