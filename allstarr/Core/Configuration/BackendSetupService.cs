using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Configuration;

public sealed class BackendSetupRequest
{
    public string Backend { get; set; } = "Jellyfin";
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseForLibraryScans { get; set; }
}

public sealed class BackendSetupException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed partial class BackendSetupService(IDbContextFactory<AllstarrDbContext> factory, IConfiguration configuration,
    DurableStorageState storage, DurableRuntimeSettingsService settings, EncryptedSecretStore secrets,
    IBackendConnectionVerifier verifier, IPlatformClock clock)
{
    public bool IsConfigured => configuration["Setup:BackendConfigured"] == "true";

    public async Task SaveAsync(BackendSetupRequest request, CancellationToken token)
    {
        if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            throw new BackendSetupException("storage_unavailable", "The data folder is not ready. Check its mount and restart Allstarr.");
        if (IsConfigured) throw new BackendSetupException("backend_already_configured", "A media server is already configured. Sign in to manage it.");
        if (!Enum.TryParse<BackendType>(request.Backend, true, out var backend) || !Enum.IsDefined(backend) ||
            request.Url.Length > 2048 || !OutboundRequestGuard.TryCreateConfiguredServiceUri(request.Url, out var uri, out _))
            throw new BackendSetupException("invalid_backend", "Choose Jellyfin or Subsonic and enter its HTTP or HTTPS base address.");
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 2000)
            throw new BackendSetupException("credentials_required", "Enter a media-server administrator username and password.");
        var endpoint = uri!.AbsoluteUri.TrimEnd('/');
        var verified = await verifier.VerifyAsync(backend, endpoint, request.Username.Trim(), request.Password, token);
        if (verified?.IsAdministrator != true)
            throw new BackendSetupException("administrator_required", "The media server did not verify an active administrator. Check the address and credentials.");
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await db.RuntimeSettings.AnyAsync(item => item.OwnerUserId == null && item.Key == "Backend:Type", token))
            throw new BackendSetupException("backend_already_configured", "Another setup request saved a media server. Reload to sign in.");
        var writes = new List<RuntimeSettingWrite>
        {
            new("Backend:Type", backend.ToString()), new("Backend:Url", endpoint), new("Identity:BackendInstanceId", "primary")
        };
        if (request.UseForLibraryScans && backend == BackendType.Jellyfin && !string.IsNullOrWhiteSpace(verified.AccessToken))
        {
            var bytes = Encoding.UTF8.GetBytes(verified.AccessToken);
            try
            {
                var credential = await secrets.StoreWithinTransactionAsync(db, null,
                    StartupConfiguration.ServicePurpose(backend.ToString(), endpoint), bytes, cancellationToken: token);
                writes.Add(new("Backend:ServiceCredentialReference", credential.Id.ToString("D")));
                writes.Add(new("Backend:ServiceUserId", verified.UserId));
                writes.Add(new("Backend:ServiceCredentialKind", "user-token"));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        await settings.StageBatchAsync(db, writes, "guided-setup", cancellationToken: token);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            Category = "onboarding",
            Action = "backend.configure",
            Outcome = "succeeded",
            CorrelationId = "initial-setup",
            DetailsJson = "{}",
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        configuration["Setup:BackendConfigured"] = "true";
        configuration["Setup:BackendRestartRequired"] = "true";
    }
}
