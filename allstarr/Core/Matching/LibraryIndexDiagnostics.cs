using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed class LibraryIndexDiagnostics(IDbContextFactory<AllstarrDbContext> factory,
    IPlatformClock clock, ILogger<LibraryIndexDiagnostics> logger)
{
    internal static string Scope(Guid? owner, LibraryIndexJobPayload payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            owner,
            payload.BackendInstanceId,
            payload.BackendPrincipalId,
            payload.BackendLibraryId
        }))));

    internal async Task ReportAsync(Guid? owner, LibraryIndexJobPayload payload, string code, bool retryable,
        string correlationId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var scope = Scope(owner, payload);
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"library-index:{scope}:{now.UtcDateTime:yyyy-MM-dd}"))[..16]);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.AuditEvents.AnyAsync(item => item.Id == id, cancellationToken)) return;
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = id,
            ActorUserId = owner,
            Category = "library-index",
            Action = "scan.failed",
            Outcome = retryable ? "retryable" : "skipped",
            SubjectType = "library-index-scope",
            SubjectId = scope,
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(new { errorCode = code }),
            CreatedAt = now
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException error) when (DbErrors.IsUniqueViolation(error)) { return; }
        logger.LogWarning("Backend audio index scope {Scope} {Outcome} ({ErrorCode}); check media-server access and library permissions",
            scope, retryable ? "will retry" : "was skipped", code);
    }
}
