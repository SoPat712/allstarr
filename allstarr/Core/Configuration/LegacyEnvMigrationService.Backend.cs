using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Configuration;

public sealed record LegacyBackendPreview(string Backend, string Endpoint, bool SavesConnection, string ServiceAccessAction, string Explanation);

public sealed partial class LegacyEnvMigrationService
{
    private sealed record BackendSnapshot(string Type, string Endpoint, string PrincipalId, string InstanceId,
        bool SavesConnection, bool ExistingServiceAccess);

    private async Task<BackendSnapshot?> BuildBackendSnapshotAsync(Guid? actor, CancellationToken token)
    {
        if (_configuration == null || !actor.HasValue ||
            !OutboundRequestGuard.TryCreateConfiguredServiceUri(_configuration[_backendSelection.EffectiveValue + ":Url"], out var endpoint, out _))
            return null;
        await using var db = await _factory.CreateDbContextAsync(token);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == actor && item.Enabled && item.IsAdmin, token);
        if (user == null || !user.BackendType.Equals(_backendSelection.EffectiveValue, StringComparison.OrdinalIgnoreCase)) return null;
        var keys = await db.RuntimeSettings.AsNoTracking().Where(item => item.OwnerUserId == null &&
            (item.Key == "Backend:Type" || item.Key == "Backend:ServiceCredentialReference")).Select(item => item.Key).ToListAsync(token);
        return new(_backendSelection.EffectiveValue, endpoint!.AbsoluteUri.TrimEnd('/'), user.BackendPrincipalId,
            user.BackendInstanceId, !keys.Contains("Backend:Type"), keys.Contains("Backend:ServiceCredentialReference"));
    }

    private static string BackendKey(PreviewState state) =>
        state.Document.Entries.FirstOrDefault(item => item.Key.Equals("JELLYFIN_API_KEY", StringComparison.OrdinalIgnoreCase))?.Value ?? "";

    private async Task<bool> CheckBackendServiceAsync(PreviewState state, CancellationToken token)
    {
        if (_backendVerifier == null || state.Backend is not { Type: "Jellyfin", ExistingServiceAccess: false } backend ||
            string.IsNullOrWhiteSpace(BackendKey(state))) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await _backendVerifier.VerifyServiceAsync(backend.Endpoint, backend.PrincipalId, BackendKey(state), deadline.Token); }
        catch (Exception error) when (!token.IsCancellationRequested && error is not OutOfMemoryException) { return false; }
    }

    private async Task<(IReadOnlyList<RuntimeSettingWrite> Writes, string ServiceAccess)> StageBackendAsync(
        AllstarrDbContext db, PreviewState state, bool verified, CancellationToken token)
    {
        if (state.Backend is not { } backend) return ([], "not_provided");
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == state.ActorUserId && user.Enabled && user.IsAdmin &&
                user.BackendType == backend.Type.ToLower() && user.BackendInstanceId == backend.InstanceId &&
                user.BackendPrincipalId == backend.PrincipalId, token))
            throw new LegacyEnvMigrationException("user_unavailable", "The verified backend administrator changed. Sign in and preview again.");
        var existing = (await db.RuntimeSettings.AsNoTracking().Where(item => item.OwnerUserId == null &&
            (item.Key.StartsWith("Backend:") || item.Key == "Identity:BackendInstanceId")).ToListAsync(token))
            .Select(DurableRuntimeSettingsService.ProjectStoredRecord).ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        if (existing.TryGetValue("Backend:Type", out var type))
        {
            if (!type.NormalizedValue.Equals(backend.Type, StringComparison.OrdinalIgnoreCase) ||
                !existing.TryGetValue("Backend:Url", out var endpoint) ||
                !OutboundRequestGuard.TryCreateConfiguredServiceUri(endpoint.NormalizedValue, out var current, out _) ||
                current!.AbsoluteUri.TrimEnd('/') != backend.Endpoint)
                throw new LegacyEnvMigrationException("state_changed", "The saved backend connection changed. Restart and preview again.");
        }
        else if (!backend.SavesConnection)
            throw new LegacyEnvMigrationException("state_changed", "The saved backend connection is no longer present. Preview again.");
        var writes = new List<RuntimeSettingWrite>();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Backend:Type"] = backend.Type,
            ["Backend:Url"] = backend.Endpoint,
            ["Identity:BackendInstanceId"] = backend.InstanceId
        })
            if (!existing.ContainsKey(key)) writes.Add(new(key, value));
        if (existing.ContainsKey("Backend:ServiceCredentialReference")) return (writes, "kept_existing");
        if (backend.Type != "Jellyfin" || string.IsNullOrWhiteSpace(BackendKey(state))) return (writes, "not_provided");
        if (!verified) return (writes, "needs_review");
        var bytes = Encoding.UTF8.GetBytes(BackendKey(state));
        try
        {
            var credential = await _secrets.StoreWithinTransactionAsync(db, null,
                StartupConfiguration.ServicePurpose(backend.Type, backend.Endpoint), bytes, cancellationToken: token);
            writes.Add(new("Backend:ServiceCredentialReference", credential.Id.ToString("D")));
            if (!existing.ContainsKey("Backend:ServiceUserId")) writes.Add(new("Backend:ServiceUserId", backend.PrincipalId));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return (writes, "verified");
    }

    private static LegacyBackendPreview? BackendPreview(BackendSnapshot? backend, LegacyEnvDocument document)
    {
        if (backend == null) return null;
        var hasKey = document.Entries.Any(item => item.Key.Equals("JELLYFIN_API_KEY", StringComparison.OrdinalIgnoreCase) && item.Value.Length > 0);
        return new(backend.Type, backend.Endpoint, backend.SavesConnection,
            backend.ExistingServiceAccess ? "kept_existing" : hasKey && backend.Type == "Jellyfin" ? "verify_then_encrypt" : "not_provided",
            "Save the active verified media-server connection without switching servers. Existing saved values win. Service access is encrypted only after its administrator permissions are verified; the importing administrator anchors its identity. Allstarr restarts after saving new connection or service-access values.");
    }
}
