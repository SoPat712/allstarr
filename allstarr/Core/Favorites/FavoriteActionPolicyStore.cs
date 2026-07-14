using allstarr.Core.Storage;
using allstarr.Core.Operations;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public enum FavoriteActionPolicyScope { Global, User }

public sealed class FavoriteActionPolicyRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public FavoriteActionPolicyScope Scope { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string BackendInstanceId { get; set; } = string.Empty;
    public string? LibraryScopeId { get; set; }
    public bool? AddToVirtualLiked { get; set; }
    public bool? MatchLocalLibrary { get; set; }
    public bool? AutoDownload { get; set; }
    public bool? EnrichMetadata { get; set; }
    public bool? PlaceManagedFile { get; set; }
    public bool? RefreshBackendLibrary { get; set; }
    public Guid? TargetCredentialReferenceId { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed record FavoriteActionPolicyValues(bool? AddToVirtualLiked, bool? MatchLocalLibrary,
    bool? AutoDownload, bool? EnrichMetadata, bool? PlaceManagedFile, bool? RefreshBackendLibrary,
    Guid? TargetCredentialReferenceId = null);

public sealed record FavoriteActionPolicyScopeKey(Guid TenantId, Guid? OwnerUserId, string Protocol,
    string BackendInstanceId, string? LibraryScopeId);

public interface IDurableFavoriteActionPolicyResolver
{
    Task<EffectiveFavoriteActionPolicy> ResolveAsync(Guid tenantId, Guid ownerUserId, string protocol,
        string backendInstanceId, string? libraryScopeId, CancellationToken cancellationToken = default);
}

public sealed class DurableFavoriteActionPolicyResolver(
    IDbContextFactory<AllstarrDbContext> factory,
    FavoriteActionPolicyOptions defaults) : IDurableFavoriteActionPolicyResolver
{
    public async Task<EffectiveFavoriteActionPolicy> ResolveAsync(Guid tenantId, Guid ownerUserId, string protocol,
        string backendInstanceId, string? libraryScopeId, CancellationToken cancellationToken = default)
    {
        var key = FavoriteActionPolicyValidation.Scope(tenantId, ownerUserId, protocol, backendInstanceId, libraryScopeId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var policies = await db.FavoriteActionPolicies.AsNoTracking().Where(item => item.TenantId == key.TenantId &&
            item.Protocol == key.Protocol && item.BackendInstanceId == key.BackendInstanceId &&
            item.LibraryScopeId == key.LibraryScopeId &&
            (item.Scope == FavoriteActionPolicyScope.Global || item.OwnerUserId == key.OwnerUserId))
            .ToListAsync(cancellationToken);
        var global = policies.SingleOrDefault(item => item.Scope == FavoriteActionPolicyScope.Global);
        var user = policies.SingleOrDefault(item => item.Scope == FavoriteActionPolicyScope.User);
        bool Resolve(Func<FavoriteActionPolicyRecord, bool?> pick, bool fallback) =>
            user == null ? global == null ? fallback : pick(global) ?? fallback : pick(user) ?? (global == null ? fallback : pick(global) ?? fallback);
        var source = user != null ? $"user-backend-override:{user.Revision}" :
            global != null ? $"tenant-backend-policy:{global.Revision}" : "configured-default";
        var credentialReferenceId = user?.TargetCredentialReferenceId ?? global?.TargetCredentialReferenceId;
        return new(
            Resolve(item => item.AddToVirtualLiked, defaults.AddToVirtualLiked),
            Resolve(item => item.MatchLocalLibrary, defaults.MatchLocalLibrary),
            Resolve(item => item.AutoDownload, defaults.AutoDownload),
            Resolve(item => item.EnrichMetadata, defaults.EnrichMetadata),
            Resolve(item => item.PlaceManagedFile, defaults.PlaceManagedFile),
            Resolve(item => item.RefreshBackendLibrary, defaults.RefreshBackendLibrary), source,
            credentialReferenceId);
    }
}

public sealed class FavoriteActionPolicyStore(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock,
    FavoriteActionPolicyOptions defaults)
{
    public async Task<FavoriteActionPolicyRecord> UpsertAsync(FavoriteActionPolicyScopeKey key,
        FavoriteActionPolicyScope scope, FavoriteActionPolicyValues values, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        key = FavoriteActionPolicyValidation.Scope(key.TenantId, key.OwnerUserId, key.Protocol, key.BackendInstanceId, key.LibraryScopeId);
        if (actorUserId == Guid.Empty || scope == FavoriteActionPolicyScope.Global && key.OwnerUserId != null ||
            scope == FavoriteActionPolicyScope.User && key.OwnerUserId == null ||
            scope == FavoriteActionPolicyScope.Global && !AllValues(values) ||
            scope == FavoriteActionPolicyScope.User && !AnyValue(values))
            throw new ArgumentException("The favorite action policy scope is invalid.", nameof(key));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AsNoTracking().AnyAsync(item => item.TenantId == key.TenantId && item.Id == actorUserId, cancellationToken) ||
            key.OwnerUserId is { } owner && !await db.Users.AsNoTracking().AnyAsync(item => item.TenantId == key.TenantId && item.Id == owner, cancellationToken))
            throw new UnauthorizedAccessException("The favorite action policy actor or owner is outside this tenant.");
        var global = scope == FavoriteActionPolicyScope.User
            ? await db.FavoriteActionPolicies.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == key.TenantId &&
                item.OwnerUserId == null && item.Scope == FavoriteActionPolicyScope.Global && item.Protocol == key.Protocol &&
                item.BackendInstanceId == key.BackendInstanceId && item.LibraryScopeId == key.LibraryScopeId, cancellationToken)
            : null;
        bool Effective(Func<FavoriteActionPolicyValues, bool?> local, Func<FavoriteActionPolicyRecord, bool?> inherited, bool fallback) =>
            local(values) ?? (global == null ? fallback : inherited(global) ?? fallback);
        var download = Effective(value => value.AutoDownload, item => item.AutoDownload, defaults.AutoDownload);
        var place = Effective(value => value.PlaceManagedFile, item => item.PlaceManagedFile, defaults.PlaceManagedFile);
        var enrich = Effective(value => value.EnrichMetadata, item => item.EnrichMetadata, defaults.EnrichMetadata);
        if (place && !download || enrich && !place)
            throw new ArgumentException("Favorite action policy dependencies require download before placement and placement before enrichment.", nameof(values));
        var effectiveRefresh = Effective(value => value.RefreshBackendLibrary, item => item.RefreshBackendLibrary,
            defaults.RefreshBackendLibrary);
        var effectiveCredential = values.TargetCredentialReferenceId ?? global?.TargetCredentialReferenceId;
        if (key.Protocol == "jellyfin" && effectiveCredential.HasValue ||
            key.Protocol == "subsonic" && effectiveRefresh && !effectiveCredential.HasValue)
            throw new ArgumentException("Subsonic refresh requires an exact-scope credential reference; Jellyfin refresh does not accept one.", nameof(values));
        if (effectiveCredential.HasValue && !await db.SecretReferences.AsNoTracking().AnyAsync(item =>
                item.Id == effectiveCredential && item.TenantId == key.TenantId && item.RevokedAt == null,
                cancellationToken))
            throw new UnauthorizedAccessException("The favorite refresh credential reference is outside this tenant or revoked.");
        var record = await db.FavoriteActionPolicies.SingleOrDefaultAsync(item => item.TenantId == key.TenantId &&
            item.OwnerUserId == key.OwnerUserId && item.Scope == scope && item.Protocol == key.Protocol &&
            item.BackendInstanceId == key.BackendInstanceId && item.LibraryScopeId == key.LibraryScopeId, cancellationToken);
        var now = clock.UtcNow;
        if (record == null)
        {
            record = new() { Id = Guid.CreateVersion7(), TenantId = key.TenantId, OwnerUserId = key.OwnerUserId,
                Scope = scope, Protocol = key.Protocol, BackendInstanceId = key.BackendInstanceId,
                LibraryScopeId = key.LibraryScopeId, CreatedAt = now };
            db.FavoriteActionPolicies.Add(record);
        }
        record.AddToVirtualLiked = values.AddToVirtualLiked; record.MatchLocalLibrary = values.MatchLocalLibrary;
        record.AutoDownload = values.AutoDownload; record.EnrichMetadata = values.EnrichMetadata;
        record.PlaceManagedFile = values.PlaceManagedFile; record.RefreshBackendLibrary = values.RefreshBackendLibrary;
        record.TargetCredentialReferenceId = values.TargetCredentialReferenceId;
        record.UpdatedByUserId = actorUserId; record.UpdatedAt = now; record.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }
    private static bool AllValues(FavoriteActionPolicyValues value) => value.AddToVirtualLiked.HasValue &&
        value.MatchLocalLibrary.HasValue && value.AutoDownload.HasValue && value.EnrichMetadata.HasValue &&
        value.PlaceManagedFile.HasValue && value.RefreshBackendLibrary.HasValue;
    private static bool AnyValue(FavoriteActionPolicyValues value) => value.AddToVirtualLiked.HasValue ||
        value.MatchLocalLibrary.HasValue || value.AutoDownload.HasValue || value.EnrichMetadata.HasValue ||
        value.PlaceManagedFile.HasValue || value.RefreshBackendLibrary.HasValue || value.TargetCredentialReferenceId.HasValue;
}

internal static class FavoriteActionPolicyValidation
{
    public static FavoriteActionPolicyScopeKey Scope(Guid tenantId, Guid? owner, string protocol, string backend, string? library)
    {
        if (tenantId == Guid.Empty || owner == Guid.Empty) throw new ArgumentException("A valid tenant and optional owner are required.");
        protocol = protocol?.Trim().ToLowerInvariant() ?? ""; backend = backend?.Trim() ?? ""; library = library?.Trim();
        if (protocol is not ("jellyfin" or "subsonic") || backend.Length is < 1 or > 200 || backend.Any(char.IsControl) ||
            library is { Length: > 300 } || library?.Any(char.IsControl) == true)
            throw new ArgumentException("The favorite action policy backend scope is invalid.");
        return new(tenantId, owner, protocol, backend, string.IsNullOrEmpty(library) ? null : library);
    }
}
