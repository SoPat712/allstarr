using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/favorite-action-policies")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class FavoriteActionPoliciesController(
    IDbContextFactory<AllstarrDbContext> factory,
    FavoriteActionPolicyStore store,
    IDurableFavoriteActionPolicyResolver resolver,
    ProviderAccountManagementOptions managementOptions) : ControllerBase
{
    private readonly ProviderAccountManagementMode _mode = managementOptions.ParseManagementMode();

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] FavoriteActionPolicyScopeRequest request, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (!TryIdentity(session, out var tenant, out var user, out error)) return error!;
        if (!TryKey(tenant, user, request, out var key, out error)) return error!;
        if (!await HasExactBackendIdentity(tenant, user, key.Protocol, key.BackendInstanceId, cancellationToken)) return NotFound();
        var effective = await resolver.ResolveAsync(tenant, user, key.Protocol, key.BackendInstanceId, key.LibraryScopeId, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var own = await db.FavoriteActionPolicies.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == tenant &&
            item.OwnerUserId == user && item.Protocol == key.Protocol && item.BackendInstanceId == key.BackendInstanceId &&
            item.LibraryScopeId == key.LibraryScopeId, cancellationToken);
        var global = session.IsAdministrator ? await db.FavoriteActionPolicies.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == tenant && item.Scope == FavoriteActionPolicyScope.Global && item.Protocol == key.Protocol &&
            item.BackendInstanceId == key.BackendInstanceId && item.LibraryScopeId == key.LibraryScopeId, cancellationToken) : null;
        return Ok(new
        {
            scope = key,
            managementMode = _mode.ToString(),
            canOverride = _mode != ProviderAccountManagementMode.AdminManaged,
            effective,
            ownOverride = own == null ? null : Values(own),
            globalPolicy = global == null ? null : Values(global)
        });
    }

    [HttpPut("me")]
    public async Task<IActionResult> PutMine([FromBody] FavoriteActionPolicyUpdateRequest request, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (_mode == ProviderAccountManagementMode.AdminManaged) return StatusCode(StatusCodes.Status403Forbidden,
            new { error = "favorite_policy_user_overrides_disabled" });
        if (!TryIdentity(session, out var tenant, out var user, out error)) return error!;
        if (!TryKey(tenant, user, request, out var key, out error)) return error!;
        if (!await HasExactBackendIdentity(tenant, user, key.Protocol, key.BackendInstanceId, cancellationToken)) return NotFound();
        try
        {
            var record = await store.UpsertAsync(key, FavoriteActionPolicyScope.User, request.Values(), user, cancellationToken);
            return Ok(new { policy = Values(record) });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = "favorite_policy_values_invalid", message = exception.Message }); }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
            new { error = "favorite_policy_credential_scope_denied" });
        }
    }

    [HttpPut("global")]
    public async Task<IActionResult> PutGlobal([FromBody] FavoriteActionPolicyUpdateRequest request, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (!session.IsAdministrator) return StatusCode(StatusCodes.Status403Forbidden);
        if (!TryIdentity(session, out var tenant, out var actor, out error)) return error!;
        if (!TryKey(tenant, null, request, out var key, out error)) return error!;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.BackendIdentities.AsNoTracking().AnyAsync(item => item.TenantId == tenant &&
            item.BackendType == key.Protocol && item.BackendInstanceId == key.BackendInstanceId, cancellationToken)) return NotFound();
        try
        {
            var record = await store.UpsertAsync(key, FavoriteActionPolicyScope.Global, request.Values(), actor, cancellationToken);
            return Ok(new { policy = Values(record) });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = "favorite_policy_values_invalid", message = exception.Message }); }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
            new { error = "favorite_policy_credential_scope_denied" });
        }
    }

    private async Task<bool> HasExactBackendIdentity(Guid tenant, Guid user, string protocol, string backend, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.BackendIdentities.AsNoTracking().AnyAsync(item => item.TenantId == tenant && item.UserId == user &&
            item.BackendType == protocol && item.BackendInstanceId == backend, token);
    }

    private static bool TryKey(Guid tenant, Guid? owner, FavoriteActionPolicyScopeRequest request,
        out FavoriteActionPolicyScopeKey key, out IActionResult? error)
    {
        try { key = FavoriteActionPolicyValidation.Scope(tenant, owner, request.Protocol, request.BackendInstanceId, request.LibraryScopeId); error = null; return true; }
        catch (ArgumentException exception) { key = null!; error = new BadRequestObjectResult(new { error = "favorite_policy_scope_invalid", message = exception.Message }); return false; }
    }
    private bool TryIdentity(AdminAuthSession session, out Guid tenant, out Guid user, out IActionResult? error)
    {
        tenant = session.TenantId ?? Guid.Empty; user = session.AllstarrUserId ?? Guid.Empty; error = null;
        if (tenant != Guid.Empty && user != Guid.Empty) return true;
        error = StatusCode(StatusCodes.Status403Forbidden, new { error = "linked_user_required" }); return false;
    }
    private bool TrySession(out AdminAuthSession session, out IActionResult? error)
    {
        if (HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) && value is AdminAuthSession current)
        { session = current; error = null; return true; }
        session = null!; error = Unauthorized(new { error = "Authentication required" }); return false;
    }
    private static object Values(FavoriteActionPolicyRecord item) => new
    {
        item.Id,
        scope = item.Scope.ToString(),
        item.AddToVirtualLiked,
        item.MatchLocalLibrary,
        item.AutoDownload,
        item.EnrichMetadata,
        item.PlaceManagedFile,
        item.RefreshBackendLibrary,
        item.TargetCredentialReferenceId,
        item.Revision,
        item.UpdatedAt
    };
}

public class FavoriteActionPolicyScopeRequest
{
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public string? LibraryScopeId { get; set; }
}
public sealed class FavoriteActionPolicyUpdateRequest : FavoriteActionPolicyScopeRequest
{
    public bool? AddToVirtualLiked { get; set; }
    public bool? MatchLocalLibrary { get; set; }
    public bool? AutoDownload { get; set; }
    public bool? EnrichMetadata { get; set; }
    public bool? PlaceManagedFile { get; set; }
    public bool? RefreshBackendLibrary { get; set; }
    public Guid? TargetCredentialReferenceId { get; set; }
    public FavoriteActionPolicyValues Values() => new(AddToVirtualLiked, MatchLocalLibrary, AutoDownload,
        EnrichMetadata, PlaceManagedFile, RefreshBackendLibrary, TargetCredentialReferenceId);
}
