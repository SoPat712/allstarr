using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.AudioMuse;

public sealed record AudioMuseConnection(string BaseUrl, bool KeyConfigured, long? Revision);
internal sealed record AudioMuseEndpoint(Uri BaseUri, Guid KeyReference);

public sealed class AudioMuseSettings(IDbContextFactory<AllstarrDbContext> factory,
    DurableRuntimeSettingsService settings, EncryptedSecretStore secrets)
{
    internal const string UrlKey = "AudioMuse:BaseUrl";
    internal const string ReferenceKey = "AudioMuse:KeyReference";
    internal const string Purpose = "audiomuse-key";

    public async Task<AudioMuseConnection> GetAsync(CancellationToken token)
    {
        var values = await settings.GetManyAsync([UrlKey, ReferenceKey], token);
        await using var db = await factory.CreateDbContextAsync(token);
        var configured = Guid.TryParse(values[ReferenceKey].NormalizedValue, out var reference) && await db.SecretReferences.AnyAsync(r =>
            r.Id == reference && r.UserId == null && r.Purpose == Purpose && r.RevokedAt == null, token);
        return new(values[UrlKey].NormalizedValue, configured, values[UrlKey].Revision);
    }

    internal async Task<AudioMuseEndpoint?> EndpointAsync(CancellationToken token)
    {
        var values = await settings.GetManyAsync([UrlKey, ReferenceKey], token);
        if (values[UrlKey].NormalizedValue.Length == 0) return null;
        if (!TryBaseUri(values[UrlKey].NormalizedValue, out var uri) ||
            !Guid.TryParse(values[ReferenceKey].NormalizedValue, out var reference))
            throw new AudioMuseUnavailable("not_set_up");
        return new(uri!, reference);
    }

    public async Task ApplyAsync(AdminAuthSession actor, string baseUrl, string? apiKey, long? revision, CancellationToken token)
    {
        baseUrl = baseUrl.Trim();
        if (baseUrl.Length > 2048 || baseUrl.Length > 0 && !TryBaseUri(baseUrl, out _) ||
            apiKey is { Length: > 4096 } || apiKey?.Any(char.IsControl) == true)
            throw new ArgumentException("Supply an HTTP URL and a key without control characters.");
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        var old = await db.RuntimeSettings.AsNoTracking().SingleOrDefaultAsync(r => r.OwnerUserId == null && r.Key == ReferenceKey, token);
        var reference = old != null && Guid.TryParse(DurableRuntimeSettingsService.ProjectStoredRecord(old).NormalizedValue, out var id) ? id : (Guid?)null;
        if (apiKey != null)
        {
            if (apiKey.Length == 0)
            {
                if (reference.HasValue)
                {
                    var secret = await db.SecretReferences.SingleAsync(r => r.Id == reference && r.UserId == null && r.Purpose == Purpose, token);
                    secret.RevokedAt = DateTimeOffset.UtcNow;
                }
                reference = null;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(apiKey);
                try { reference = (await secrets.StoreWithinTransactionAsync(db, null, Purpose, bytes, reference, token)).Id; }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        var staged = await settings.StageBatchAsync(db,
            [new(UrlKey, baseUrl, revision), new(ReferenceKey, reference?.ToString("D") ?? "", old?.Revision)],
            "admin-ui", actor.AllstarrUserId, token);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw new RuntimeSettingConflictException("Connection changed. Reload before saving."); }
        await transaction.CommitAsync(token);
        settings.PublishExternalCommit(staged.Select(s => DurableRuntimeSettingsService.ProjectStoredRecord(s.Record)).ToArray());
    }

    internal static bool TryBaseUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") ||
            parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0) return false;
        uri = new(parsed.AbsoluteUri.TrimEnd('/') + "/");
        return true;
    }
}

internal sealed class AudioMuseUnavailable(string reason) : Exception
{
    internal string Reason { get; } = reason;
}
