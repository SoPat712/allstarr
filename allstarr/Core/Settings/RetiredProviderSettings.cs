using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Settings;

internal static class RetiredProviderSettings
{
    internal static bool IsRetired(string id) => id.Trim().Equals("binilyrics", StringComparison.OrdinalIgnoreCase);

    internal static async Task RemoveAsync(AllstarrDbContext db, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var settings = await db.RuntimeSettings.ToListAsync(token);
        foreach (var row in settings)
        {
            if (row.Key.Split(':').Any(IsRetired) ||
                row.Key.Equals("SpotifyApi:Enabled", StringComparison.OrdinalIgnoreCase) ||
                row.Key.Equals("MusicBrainz:Enabled", StringComparison.OrdinalIgnoreCase))
            {
                db.RuntimeSettings.Remove(row);
                continue;
            }
            string? cleaned = null;
            if (row.Key == ProviderUsePolicy.SettingKey)
            {
                var raw = JsonSerializer.Deserialize<string>(row.ValueJson)!;
                using var document = JsonDocument.Parse(raw);
                if (!document.RootElement.EnumerateObject().Any(item => IsRetired(item.Name))) continue;
                var modes = ProviderUsePolicy.ParseModes(raw);
                if (modes.Count == 0)
                {
                    db.RuntimeSettings.Remove(row);
                    continue;
                }
                cleaned = JsonSerializer.Serialize(ProviderUsePolicy.SerializeModes(modes));
            }
            else if (row.Key.StartsWith("Providers:", StringComparison.Ordinal) && row.ValueType == RuntimeSettingValueType.StringList)
            {
                var ids = JsonSerializer.Deserialize<string[]>(row.ValueJson)!;
                if (!ids.Any(IsRetired)) continue;
                cleaned = JsonSerializer.Serialize(ids.Where(id => !IsRetired(id)));
            }
            if (cleaned == null) continue;
            row.ValueJson = cleaned;
            row.Revision++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        var accounts = await db.ProviderAccounts.Where(row => row.ProviderId.ToLower() == "binilyrics").ToListAsync(token);
        foreach (var account in accounts)
        {
            if (ProviderAccountSettings.IsRemoved(account) && !account.Enabled && account.SecretReferenceId == null) continue;
            account.Enabled = false;
            account.SecretReferenceId = null;
            account.SettingsJson = JsonSerializer.Serialize(new Dictionary<string, bool> { [ProviderAccountSettings.RemovedSetting] = true });
            account.Revision++;
            account.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(token);
        await db.AuditEvents.Where(row => (row.Category == "provider-check" || row.Category == "provider-status") &&
            row.SubjectId != null && row.SubjectId.ToLower().StartsWith("binilyrics/"))
            .ExecuteDeleteAsync(token);
        await db.RecordingLyricsChoices.Where(row => row.SourceId.ToLower() == "binilyrics").ExecuteDeleteAsync(token);
        var secrets = db.SecretReferences.Where(row => row.Purpose.ToLower() == "binilyrics" ||
            row.Purpose.ToLower().StartsWith("binilyrics:") || row.Purpose.ToLower().StartsWith("provider-account:binilyrics:"));
        await db.ProviderAccounts.Where(row => row.SecretReferenceId != null && secrets.Any(secret => secret.Id == row.SecretReferenceId))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.SecretReferenceId, (Guid?)null), token);
        await secrets.ExecuteDeleteAsync(token);
        await transaction.CommitAsync(token);
    }
}
