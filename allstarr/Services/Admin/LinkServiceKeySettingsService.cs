using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Admin;

/// <summary>An API key for a service that finds a song on other services, stored encrypted and loaded at startup.</summary>
public sealed record LinkServiceKey(string Field, string Name, string ReferenceKey, string ConfigKey, string Purpose)
{
    public static readonly LinkServiceKey SongLink = new("ODESLI_API_KEY", "song.link", "Odesli:ApiKeyReference", "Odesli:ApiKey", "odesli-api-key");
    public static readonly LinkServiceKey MusicLink = new("MUSICLINK_API_KEY", "MusicLink", "MusicLink:ApiKeyReference", "MusicLink:ApiKey", "musiclink-api-key");
    public static readonly IReadOnlyList<LinkServiceKey> All = [SongLink, MusicLink];
}

public sealed class LinkServiceKeySettingsService(IDbContextFactory<AllstarrDbContext> factory,
    DurableRuntimeSettingsService settings, EncryptedSecretStore secrets, IPlatformClock clock)
{
    public async Task<RuntimeSettingBatchResult> ApplyAsync(AdminAuthSession actor, LinkServiceKey key, string apiKey,
        long? expectedRevision, IReadOnlyList<RuntimeSettingWrite> writes, CancellationToken token)
    {
        if (apiKey.Length > 4096 || apiKey.Any(char.IsControl))
            throw new ArgumentException($"The {key.Name} API key must be at most 4096 characters without control characters.");
        apiKey = apiKey.Trim();
        if (writes.Any(item => item.Key.Equals(key.ReferenceKey, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"The {key.Name} key reference cannot be supplied directly.");
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        var row = await db.RuntimeSettings.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OwnerUserId == null && item.Key == key.ReferenceKey, token);
        if (row?.Revision != expectedRevision)
            throw new RuntimeSettingConflictException($"The {key.Name} API key changed. Reload before saving.");
        var oldId = row != null && Guid.TryParse(DurableRuntimeSettingsService.ProjectStoredRecord(row).NormalizedValue, out var id)
            ? id : (Guid?)null;
        var old = oldId.HasValue ? await db.SecretReferences.SingleOrDefaultAsync(item =>
            item.Id == oldId && item.UserId == null && item.Purpose == key.Purpose && item.RevokedAt == null, token) : null;
        Guid? reference = null;
        if (apiKey.Length > 0)
        {
            var bytes = Encoding.UTF8.GetBytes(apiKey);
            try { reference = (await secrets.StoreWithinTransactionAsync(db, null, key.Purpose, bytes, old?.Id, token)).Id; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else if (old != null) old.RevokedAt = clock.UtcNow;
        var staged = await settings.StageBatchAsync(db,
            writes.Append(new RuntimeSettingWrite(key.ReferenceKey, reference?.ToString("D") ?? "", expectedRevision)).ToArray(),
            "admin-ui", actor.AllstarrUserId, token);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actor.AllstarrUserId,
            Category = "runtime-settings",
            Action = key == LinkServiceKey.SongLink ? "odesli.configure" : "link-service.configure",
            Outcome = "succeeded",
            CorrelationId = "runtime-settings",
            DetailsJson = JsonSerializer.Serialize(new
            {
                service = key.Name,
                keyConfigured = reference.HasValue,
                settings = staged.Where(item => item.Record.Key != key.ReferenceKey).Select(item => item.Record.Key).ToArray()
            }),
            CreatedAt = clock.UtcNow
        });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw new RuntimeSettingConflictException($"The {key.Name} settings changed. Reload before saving."); }
        await transaction.CommitAsync(token);
        var committed = staged.Select(item => DurableRuntimeSettingsService.ProjectStoredRecord(item.Record)).ToArray();
        return new(committed, settings.PublishExternalCommit(committed));
    }

    internal static async Task LoadForStartupAsync(IServiceProvider services, IConfiguration configuration, CancellationToken token)
    {
        foreach (var key in LinkServiceKey.All)
        {
            var value = configuration[key.ReferenceKey];
            if (value == null) continue;
            configuration[key.ConfigKey] = "";
            if (!Guid.TryParse(value, out var reference)) continue;
            try
            {
                using var lease = await services.GetRequiredService<EncryptedSecretStore>().OpenAsync(reference,
                    new SecretAccessContext(null, key.Purpose, AllowShared: true), token);
                configuration[key.ConfigKey] = lease.ReadUtf8();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or KeyNotFoundException)
            {
                configuration[key.ConfigKey] = "";
            }
        }
    }
}
