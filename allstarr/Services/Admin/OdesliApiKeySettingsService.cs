using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Admin;

public sealed class OdesliApiKeySettingsService(IDbContextFactory<AllstarrDbContext> factory,
    DurableRuntimeSettingsService settings, EncryptedSecretStore secrets, IPlatformClock clock)
{
    internal const string ReferenceKey = "Odesli:ApiKeyReference";
    private const string Purpose = "odesli-api-key";

    public async Task<RuntimeSettingBatchResult> ApplyAsync(AdminAuthSession actor, string apiKey,
        long? expectedRevision, IReadOnlyList<RuntimeSettingWrite> writes, CancellationToken token)
    {
        if (apiKey.Length > 4096 || apiKey.Any(char.IsControl))
            throw new ArgumentException("The song.link API key must be at most 4096 characters without control characters.");
        apiKey = apiKey.Trim();
        if (writes.Any(item => item.Key.Equals(ReferenceKey, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The song.link key reference cannot be supplied directly.");
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        var row = await db.RuntimeSettings.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OwnerUserId == null && item.Key == ReferenceKey, token);
        if (row?.Revision != expectedRevision)
            throw new RuntimeSettingConflictException("The song.link API key changed. Reload before saving.");
        var oldId = row != null && Guid.TryParse(DurableRuntimeSettingsService.ProjectStoredRecord(row).NormalizedValue, out var id)
            ? id : (Guid?)null;
        var old = oldId.HasValue ? await db.SecretReferences.SingleOrDefaultAsync(item =>
            item.Id == oldId && item.UserId == null && item.Purpose == Purpose && item.RevokedAt == null, token) : null;
        Guid? reference = null;
        if (apiKey.Length > 0)
        {
            var bytes = Encoding.UTF8.GetBytes(apiKey);
            try { reference = (await secrets.StoreWithinTransactionAsync(db, null, Purpose, bytes, old?.Id, token)).Id; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else if (old != null) old.RevokedAt = clock.UtcNow;
        var staged = await settings.StageBatchAsync(db,
            writes.Append(new RuntimeSettingWrite(ReferenceKey, reference?.ToString("D") ?? "", expectedRevision)).ToArray(),
            "admin-ui", actor.AllstarrUserId, token);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actor.AllstarrUserId,
            Category = "runtime-settings",
            Action = "odesli.configure",
            Outcome = "succeeded",
            CorrelationId = "runtime-settings",
            DetailsJson = JsonSerializer.Serialize(new
            {
                keyConfigured = reference.HasValue,
                settings = staged.Where(item => item.Record.Key != ReferenceKey).Select(item => item.Record.Key).ToArray()
            }),
            CreatedAt = clock.UtcNow
        });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw new RuntimeSettingConflictException("The song.link settings changed. Reload before saving."); }
        await transaction.CommitAsync(token);
        var committed = staged.Select(item => DurableRuntimeSettingsService.ProjectStoredRecord(item.Record)).ToArray();
        return new(committed, settings.PublishExternalCommit(committed));
    }

    internal static async Task LoadForStartupAsync(IServiceProvider services, IConfiguration configuration, CancellationToken token)
    {
        var value = configuration[ReferenceKey];
        if (value == null) return;
        configuration["Odesli:ApiKey"] = "";
        if (!Guid.TryParse(value, out var reference)) return;
        try
        {
            using var lease = await services.GetRequiredService<EncryptedSecretStore>().OpenAsync(reference,
                new SecretAccessContext(null, Purpose, AllowShared: true), token);
            configuration["Odesli:ApiKey"] = lease.ReadUtf8();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or KeyNotFoundException)
        {
            configuration["Odesli:ApiKey"] = "";
        }
    }
}
