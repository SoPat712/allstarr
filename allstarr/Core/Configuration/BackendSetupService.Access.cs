using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Configuration;

public sealed class BackendServiceAccessRequest
{
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ExpectedRevision { get; set; } = "";
}

public sealed record BackendServiceAccessSnapshot(bool Supported, bool ConnectionSaved, bool Configured, bool NeedsReview, string Revision, string CredentialKind);

public sealed partial class BackendSetupService
{
    private static readonly string[] AccessKeys = ["Backend:Type", "Backend:Url", "Identity:BackendInstanceId",
        "Backend:ServiceCredentialReference", "Backend:ServiceUserId", "Backend:ServiceCredentialKind"];

    public async Task<BackendServiceAccessSnapshot> GetServiceAccessAsync(AdminAuthSession actor, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        return AccessSnapshot(await ReadAccessAsync(db, token));
    }

    public async Task SaveServiceAccessAsync(AdminAuthSession actor, BackendServiceAccessRequest request, CancellationToken token)
    {
        Dictionary<string, RuntimeSettingRecord> before;
        await using (var read = await factory.CreateDbContextAsync(token))
        {
            await AdminSettingsAccess.RequireActiveAsync(read, actor, token);
            before = await ReadAccessAsync(read, token);
        }
        var snapshot = AccessSnapshot(before);
        if (snapshot.Revision != request.ExpectedRevision)
            throw new RuntimeSettingConflictException("Media-server access changed. Reload before saving.");
        if (!snapshot.Supported || !snapshot.ConnectionSaved)
            throw new ArgumentException("Import or save the Jellyfin connection first. Subsonic playlist access is managed separately for each listener.");
        var endpoint = DurableRuntimeSettingsService.ProjectStoredRecord(before["Backend:Url"]).NormalizedValue;
        var instance = DurableRuntimeSettingsService.ProjectStoredRecord(before["Identity:BackendInstanceId"]).NormalizedValue;
        if (actor.BackendType != "Jellyfin" || actor.BackendInstanceId != instance ||
            configuration["Backend:Type"] != "Jellyfin" || configuration["Jellyfin:Url"]?.TrimEnd('/') != endpoint.TrimEnd('/'))
            throw new RuntimeSettingConflictException("The saved media server differs from this session. Restart and sign in again.");
        AdminOidcBackendUser? verified = null;
        if (request.Enabled)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 200 || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 2000)
                throw new ArgumentException("Enter your media-server administrator username and password.");
            verified = await verifier.VerifyAsync(BackendType.Jellyfin, endpoint, request.Username.Trim(), request.Password, token);
            if (verified is not { IsAdministrator: true, AccessToken.Length: > 0 } || verified.UserId != actor.UserId)
                throw new UnauthorizedAccessException("Verify the same active media-server administrator that is signed in.");
        }
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        var current = await ReadAccessAsync(db, token);
        if (AccessSnapshot(current).Revision != snapshot.Revision)
            throw new RuntimeSettingConflictException("Media-server access changed during verification. Reload before saving.");
        var purpose = StartupConfiguration.ServicePurpose("Jellyfin", endpoint);
        var oldReference = current.TryGetValue("Backend:ServiceCredentialReference", out var previous) &&
            Guid.TryParse(DurableRuntimeSettingsService.ProjectStoredRecord(previous).NormalizedValue, out var old) ? old : (Guid?)null;
        var reusable = oldReference.HasValue && await db.SecretReferences.AnyAsync(item => item.Id == oldReference &&
            item.UserId == null && item.Purpose == purpose && item.RevokedAt == null, token) ? oldReference : null;
        Guid? reference = null;
        if (request.Enabled)
        {
            var bytes = Encoding.UTF8.GetBytes(verified!.AccessToken!);
            try { reference = (await secrets.StoreWithinTransactionAsync(db, null, purpose, bytes, reusable, token)).Id; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else if (reusable.HasValue)
        {
            var revoked = await db.SecretReferences.SingleAsync(item => item.Id == reusable, token);
            revoked.RevokedAt ??= clock.UtcNow;
        }
        RuntimeSettingWrite Write(string key, string value) => new(key, value, current.TryGetValue(key, out var row) ? row.Revision : null);
        await settings.StageBatchAsync(db, [Write("Backend:ServiceCredentialReference", reference?.ToString("D") ?? ""),
            Write("Backend:ServiceUserId", verified?.UserId ?? ""),
            Write("Backend:ServiceCredentialKind", reference.HasValue ? "user-token" : "")], "backend-access", actor.AllstarrUserId, token);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actor.AllstarrUserId,
            Category = "onboarding",
            Action = "backend.service-access",
            Outcome = "succeeded",
            CorrelationId = "backend-access",
            DetailsJson = request.Enabled ? "{\"enabled\":true}" : "{\"enabled\":false}",
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private BackendServiceAccessSnapshot AccessSnapshot(Dictionary<string, RuntimeSettingRecord> rows) => new(
        configuration["Backend:Type"] == "Jellyfin", rows.ContainsKey("Backend:Type") && rows.ContainsKey("Backend:Url") && rows.ContainsKey("Identity:BackendInstanceId"),
        rows.TryGetValue("Backend:ServiceCredentialReference", out var reference) && Guid.TryParse(DurableRuntimeSettingsService.ProjectStoredRecord(reference).NormalizedValue, out _),
        configuration["Backend:Type"] == "Jellyfin" && (configuration["Setup:ServiceCredentialNeedsReview"] == "true" ||
            string.IsNullOrWhiteSpace(configuration["Jellyfin:ApiKey"])),
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';', rows.Values.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + ":" + item.Id.ToString("N") + ":" + item.Revision))))),
        rows.TryGetValue("Backend:ServiceCredentialKind", out var kind) ? DurableRuntimeSettingsService.ProjectStoredRecord(kind).NormalizedValue : "");

    private static Task<Dictionary<string, RuntimeSettingRecord>> ReadAccessAsync(AllstarrDbContext db, CancellationToken token) =>
        db.RuntimeSettings.AsNoTracking().Where(item => item.OwnerUserId == null && AccessKeys.Contains(item.Key)).ToDictionaryAsync(item => item.Key, token);
}
