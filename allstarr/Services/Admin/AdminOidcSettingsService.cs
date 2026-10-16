using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Admin;

public sealed class AdminOidcSettingsRequest
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public string DisplayName { get; set; } = "Single sign-on";
    public string? ClientSecret { get; set; }
    public bool RemoveClientSecret { get; set; }
    public string ExpectedRevision { get; set; } = "";
}

public sealed record AdminOidcSettingsSnapshot(bool Enabled, string Authority, string ClientId, string PublicUrl,
    string DisplayName, bool HasClientSecret, bool NeedsReview, string Revision);

public sealed class AdminOidcSettingsService(IDbContextFactory<AllstarrDbContext> factory,
    DurableRuntimeSettingsService settings, EncryptedSecretStore secrets, IConfiguration configuration, IPlatformClock clock)
{
    private static readonly string[] Keys = ["Admin:Oidc:Enabled", "Admin:Oidc:Authority", "Admin:Oidc:ClientId",
        "Admin:Oidc:PublicUrl", "Admin:Oidc:DisplayName", "Admin:Oidc:ClientSecretReference"];

    public async Task<AdminOidcSettingsSnapshot> GetAsync(AdminAuthSession actor, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        var rows = await ReadAsync(db, token);
        string Value(string field) => Read(rows, field);
        var reference = ParseReference(Value("ClientSecretReference"));
        var purpose = Purpose(Value("Authority"), Value("ClientId"));
        var hasSecret = reference.HasValue && await db.SecretReferences.AnyAsync(item => item.Id == reference &&
            item.UserId == null && item.Purpose == purpose && item.RevokedAt == null, token);
        return new(Value("Enabled") == "true", Value("Authority"), Value("ClientId"), Value("PublicUrl"),
            Value("DisplayName"), hasSecret, configuration["Setup:OidcNeedsReview"] == "true", Revision(rows));
    }

    public async Task SaveAsync(AdminAuthSession actor, AdminOidcSettingsRequest request, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        var rows = await ReadAsync(db, token);
        if (Revision(rows) != request.ExpectedRevision)
            throw new RuntimeSettingConflictException("Single sign-on changed in another tab. Reload before saving.");
        var authority = request.Authority.Trim().TrimEnd('/');
        var clientId = request.ClientId.Trim();
        var purpose = Purpose(authority, clientId);
        var oldReference = ParseReference(Read(rows, "ClientSecretReference"));
        var sameTarget = authority == Read(rows, "Authority") && clientId == Read(rows, "ClientId");
        var reference = sameTarget && !request.RemoveClientSecret ? oldReference : null;
        if (reference.HasValue && !await db.SecretReferences.AnyAsync(item => item.Id == reference &&
            item.UserId == null && item.Purpose == purpose && item.RevokedAt == null, token)) reference = null;
        if (request.RemoveClientSecret && !string.IsNullOrEmpty(request.ClientSecret))
            throw new ArgumentException("Choose either a replacement client secret or removal.");
        if (request.ClientSecret is { Length: > 4096 }) throw new ArgumentException("The client secret is too long.");
        var options = new AdminOidcOptions
        {
            Enabled = request.Enabled,
            Authority = authority,
            ClientId = clientId,
            PublicUrl = request.PublicUrl.Trim(),
            DisplayName = request.DisplayName.Trim(),
            ClientSecret = !string.IsNullOrEmpty(request.ClientSecret) || reference.HasValue ? "configured" : ""
        };
        try { options.Validate(new AdminBasePath(configuration).Value); }
        catch (InvalidOperationException) { throw new ArgumentException("Enable single sign-on only with HTTPS identity and public URLs, the configured admin path, a client ID and a client secret. Changing identity service or client ID requires a new secret."); }
        if (!string.IsNullOrEmpty(request.ClientSecret))
        {
            var bytes = Encoding.UTF8.GetBytes(request.ClientSecret);
            try { reference = (await secrets.StoreWithinTransactionAsync(db, null, purpose, bytes, reference, token)).Id; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (oldReference.HasValue && oldReference != reference)
        {
            var oldPurpose = Purpose(Read(rows, "Authority"), Read(rows, "ClientId"));
            var old = await db.SecretReferences.SingleOrDefaultAsync(item => item.Id == oldReference && item.UserId == null && item.Purpose == oldPurpose, token);
            if (old != null) old.RevokedAt ??= clock.UtcNow;
        }
        var values = new Dictionary<string, string>
        {
            ["Enabled"] = request.Enabled ? "true" : "false",
            ["Authority"] = authority,
            ["ClientId"] = clientId,
            ["PublicUrl"] = options.PublicUrl,
            ["DisplayName"] = options.DisplayName,
            ["ClientSecretReference"] = reference?.ToString("D") ?? ""
        };
        await settings.StageBatchAsync(db, values.Select(item => new RuntimeSettingWrite("Admin:Oidc:" + item.Key,
            item.Value, rows.TryGetValue("Admin:Oidc:" + item.Key, out var row) ? row.Revision : null)).ToArray(),
            "admin-access", actor.AllstarrUserId, token);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actor.AllstarrUserId,
            Category = "admin-access",
            Action = "oidc.configure",
            Outcome = "succeeded",
            CorrelationId = "admin-access",
            DetailsJson = JsonSerializer.Serialize(new { request.Enabled, secretReplaced = !string.IsNullOrEmpty(request.ClientSecret) }),
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    internal static string Purpose(string authority, string clientId) => "admin-oidc:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(authority.TrimEnd('/') + "\n" + clientId)));

    internal static async Task LoadForStartupAsync(IServiceProvider services, IConfiguration configuration, CancellationToken token)
    {
        configuration["Admin:Oidc:ClientSecret"] = "";
        if (!configuration.GetValue("Admin:Oidc:Enabled", false)) return;
        try
        {
            if (!Guid.TryParse(configuration["Admin:Oidc:ClientSecretReference"], out var reference))
                throw new InvalidOperationException("Single sign-on needs an encrypted client secret.");
            using var lease = await services.GetRequiredService<EncryptedSecretStore>().OpenAsync(reference,
                new SecretAccessContext(null, Purpose(configuration["Admin:Oidc:Authority"] ?? "", configuration["Admin:Oidc:ClientId"] ?? ""), true), token);
            configuration["Admin:Oidc:ClientSecret"] = lease.ReadUtf8();
            (configuration.GetSection("Admin:Oidc").Get<AdminOidcOptions>() ?? new()).Validate(new AdminBasePath(configuration).Value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or KeyNotFoundException)
        {
            configuration["Admin:Oidc:ClientSecret"] = "";
            configuration["Admin:Oidc:Enabled"] = "false";
            configuration["Setup:OidcNeedsReview"] = "true";
        }
    }

    private static Guid? ParseReference(string value) => Guid.TryParse(value, out var id) ? id : null;
    private static Task<Dictionary<string, RuntimeSettingRecord>> ReadAsync(AllstarrDbContext db, CancellationToken token) =>
        db.RuntimeSettings.AsNoTracking().Where(item => item.OwnerUserId == null && Keys.Contains(item.Key)).ToDictionaryAsync(item => item.Key, token);
    private static string Read(Dictionary<string, RuntimeSettingRecord> rows, string field) =>
        rows.TryGetValue("Admin:Oidc:" + field, out var row) ? DurableRuntimeSettingsService.ProjectStoredRecord(row).NormalizedValue :
            RuntimeSettingCatalog.Require("Admin:Oidc:" + field).DefaultValue ?? "";
    private static string Revision(Dictionary<string, RuntimeSettingRecord> rows) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';', rows.Values.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + ":" + item.Id.ToString("N") + ":" + item.Revision)))));
}
