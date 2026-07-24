using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Identity;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Middleware;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/provider-accounts")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed partial class ProviderAccountsController : ControllerBase
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly EncryptedSecretStore _secretStore;
    private readonly ProviderAccountManagementMode _managementMode;

    public ProviderAccountsController(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        EncryptedSecretStore secretStore,
        ProviderAccountManagementOptions managementOptions)
    {
        _contextFactory = contextFactory;
        _secretStore = secretStore;
        _managementMode = managementOptions.ParseManagementMode();
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error))
        {
            return error!;
        }

        if (GetManagementAccessError(session) is { } accessError)
        {
            return accessError;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = ApplyManagementScope(context.ProviderAccounts.AsNoTracking(), session);

        var accounts = await query
            .OrderBy(item => item.ProviderId)
            .ThenBy(item => item.DisplayName)
            .ToListAsync(cancellationToken);
        var secretIds = accounts
            .Where(item => item.SecretReferenceId.HasValue)
            .Select(item => item.SecretReferenceId!.Value)
            .ToList();
        var secrets = await context.SecretReferences.AsNoTracking()
            .Where(item => secretIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var userIds = accounts
            .SelectMany(item => new[] { item.OwnerUserId, item.CreatedByUserId })
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .Distinct()
            .ToArray();
        var users = userIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await context.Users.AsNoTracking()
                .Where(item => userIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Ok(new
        {
            managementMode = _managementMode.ToString(),
            accounts = accounts.Select(account => AccountResponse(
                account,
                account.SecretReferenceId.HasValue &&
                secrets.TryGetValue(account.SecretReferenceId.Value, out var secret)
                    ? secret
                    : null,
                account.OwnerUserId.HasValue ? users.GetValueOrDefault(account.OwnerUserId.Value) : null,
                account.CreatedByUserId.HasValue ? users.GetValueOrDefault(account.CreatedByUserId.Value) : null))
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateProviderAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error))
        {
            return error!;
        }

        if (GetManagementAccessError(session) is { } accessError)
        {
            return accessError;
        }

        if (!TryNormalizeRequest(
                request,
                session,
                CanManageAllAccounts(session),
                out var normalized,
                out var validationError))
        {
            return BadRequest(new { error = validationError });
        }

        var scopeValidationError = await ValidateAccountScopeAsync(normalized, cancellationToken);
        if (scopeValidationError != null)
        {
            return BadRequest(new { error = scopeValidationError });
        }

        var now = DateTimeOffset.UtcNow;
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = normalized.TenantId,
            OwnerUserId = normalized.OwnerUserId,
            CreatedByUserId = session.AllstarrUserId,
            ProviderId = normalized.ProviderId,
            DisplayName = normalized.DisplayName,
            Scope = normalized.Scope,
            LibraryScopeId = normalized.LibraryScopeId,
            Enabled = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            context.ProviderAccounts.Add(account);
            await context.SaveChangesAsync(cancellationToken);
        }

        SecretReferenceInfo? storedSecret = null;
        try
        {
            if (request.Secret.HasValue && request.Secret.Value.ValueKind != JsonValueKind.Null)
            {
                var secretBytes = Encoding.UTF8.GetBytes(request.Secret.Value.GetRawText());
                storedSecret = await _secretStore.StoreAsync(
                    account.TenantId,
                    $"provider-account:{account.ProviderId}:{account.Id:N}",
                    secretBytes,
                    cancellationToken: cancellationToken);
                account.SecretReferenceId = storedSecret.Id;
            }

            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var persisted = await context.ProviderAccounts.SingleAsync(
                item => item.Id == account.Id,
                cancellationToken);
            persisted.SecretReferenceId = account.SecretReferenceId;
            persisted.Enabled = request.Enabled;
            persisted.UpdatedAt = DateTimeOffset.UtcNow;
            persisted.Revision++;
            AddAudit(
                context,
                session,
                "provider-account.created",
                "succeeded",
                new { accountId = persisted.Id, persisted.ProviderId, scope = persisted.Scope.ToString() });
            await context.SaveChangesAsync(cancellationToken);
            return CreatedAtAction(
                nameof(List),
                AccountResponse(
                    persisted,
                    storedSecret == null
                        ? null
                        : new SecretReferenceRecord
                        {
                            Id = storedSecret.Id,
                            TenantId = storedSecret.TenantId,
                            Purpose = storedSecret.Purpose,
                            ActiveVersion = storedSecret.ActiveVersion,
                            UpdatedAt = storedSecret.UpdatedAt,
                            RevokedAt = storedSecret.Revoked ? storedSecret.UpdatedAt : null
                        },
                    normalized.OwnerUserId == session.AllstarrUserId ? session.UserName : null,
                    session.UserName));
        }
        catch
        {
            if (storedSecret != null)
            {
                try
                {
                    await _secretStore.RevokeAsync(
                        storedSecret.Id,
                        new SecretAccessContext(
                            storedSecret.TenantId,
                            session.IsAdministrator && storedSecret.TenantId == null),
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure. Revoked-orphan cleanup is best effort.
                }
            }

            await using var cleanup = await _contextFactory.CreateDbContextAsync(CancellationToken.None);
            var orphan = await cleanup.ProviderAccounts.SingleOrDefaultAsync(item => item.Id == account.Id);
            if (orphan != null)
            {
                cleanup.ProviderAccounts.Remove(orphan);
                await cleanup.SaveChangesAsync();
            }

            throw;
        }
    }

    [HttpPut("{accountId:guid}/secret")]
    public async Task<IActionResult> ReplaceSecret(
        Guid accountId,
        [FromBody] ReplaceProviderSecretRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error))
        {
            return error!;
        }

        if (GetManagementAccessError(session) is { } accessError)
        {
            return accessError;
        }

        if (request.Secret.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return BadRequest(new { error = "Secret is required" });
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ApplyManagementScope(context.ProviderAccounts, session).SingleOrDefaultAsync(
            item => item.Id == accountId,
            cancellationToken);
        if (account == null)
        {
            return NotFound();
        }

        var bytes = Encoding.UTF8.GetBytes(request.Secret.GetRawText());
        var secret = await _secretStore.StoreAsync(
            account.TenantId,
            $"provider-account:{account.ProviderId}:{account.Id:N}",
            bytes,
            account.SecretReferenceId,
            cancellationToken);
        account.SecretReferenceId = secret.Id;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        account.Revision++;
        AddAudit(
            context,
            session,
            "provider-account.secret-replaced",
            "succeeded",
            new { accountId = account.Id, account.ProviderId, secretVersion = secret.ActiveVersion });
        await context.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            accountId = account.Id,
            secret = new
            {
                configured = true,
                version = secret.ActiveVersion,
                keyId = secret.KeyId,
                updatedAt = secret.UpdatedAt
            }
        });
    }

    [HttpDelete("{accountId:guid}")]
    public async Task<IActionResult> Revoke(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error))
        {
            return error!;
        }

        if (GetManagementAccessError(session) is { } accessError)
        {
            return accessError;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ApplyManagementScope(context.ProviderAccounts, session).SingleOrDefaultAsync(
            item => item.Id == accountId,
            cancellationToken);
        if (account == null)
        {
            return NotFound();
        }

        account.Enabled = false;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        account.Revision++;
        if (account.SecretReferenceId.HasValue)
        {
            await _secretStore.RevokeAsync(
                account.SecretReferenceId.Value,
                new SecretAccessContext(account.TenantId, session.IsAdministrator && account.TenantId == null),
                cancellationToken);
        }

        AddAudit(
            context,
            session,
            "provider-account.revoked",
            "succeeded",
            new { accountId = account.Id, account.ProviderId });
        await context.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("{accountId:guid}")]
    public async Task<IActionResult> SetEnabled(
        Guid accountId,
        [FromBody] SetProviderAccountEnabledRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error)) return error!;
        if (GetManagementAccessError(session) is { } accessError) return accessError;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ApplyManagementScope(context.ProviderAccounts, session).SingleOrDefaultAsync(
            item => item.Id == accountId, cancellationToken);
        if (account == null) return NotFound();
        if (request.ExpectedRevision.HasValue && request.ExpectedRevision.Value != account.Revision)
            return Conflict(new { error = "The provider account changed. Reload and try again." });
        if (request.Enabled)
        {
            if (!account.SecretReferenceId.HasValue) return BadRequest(new { error = "Configure the credential before enabling this account." });
            var revoked = await context.SecretReferences.AsNoTracking().AnyAsync(
                item => item.Id == account.SecretReferenceId.Value && item.RevokedAt != null, cancellationToken);
            if (revoked) return BadRequest(new { error = "This credential was revoked. Replace it before enabling the account." });
        }

        account.Enabled = request.Enabled;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        account.Revision++;
        AddAudit(context, session, request.Enabled ? "provider-account.enabled" : "provider-account.disabled",
            "succeeded", new { accountId = account.Id, account.ProviderId });
        await context.SaveChangesAsync(cancellationToken);
        return Ok(AccountResponse(account, null));
    }

    [HttpPut("{accountId:guid}/audience")]
    public async Task<IActionResult> UpdateAudience(
        Guid accountId,
        [FromBody] UpdateProviderAccountAudienceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error)) return error!;
        if (!CanManageAllAccounts(session)) return ManagementForbidden();
        if (!Enum.TryParse<ProviderAccountScope>(request.Scope, true, out var scope) || !Enum.IsDefined(scope))
            return BadRequest(new { error = "Audience must be Only me, Everyone, or One library." });
        var libraryScopeId = string.IsNullOrWhiteSpace(request.LibraryScopeId) ? null : request.LibraryScopeId.Trim();
        if (scope == ProviderAccountScope.Library && libraryScopeId == null)
            return BadRequest(new { error = "Choose a library for this audience." });

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ApplyManagementScope(context.ProviderAccounts, session)
            .SingleOrDefaultAsync(item => item.Id == accountId, cancellationToken);
        if (account == null) return NotFound();
        if (request.ExpectedRevision.HasValue && request.ExpectedRevision.Value != account.Revision)
            return Conflict(new { error = "The provider account changed. Reload and try again." });

        account.Scope = scope;
        account.TenantId = scope == ProviderAccountScope.Global ? null : session.TenantId;
        account.OwnerUserId = scope == ProviderAccountScope.User ? session.AllstarrUserId : null;
        account.LibraryScopeId = scope == ProviderAccountScope.Library ? libraryScopeId : null;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        account.Revision++;
        AddAudit(context, session, "provider-account.audience-updated", "succeeded", new
        {
            accountId = account.Id,
            account.ProviderId,
            audience = scope.ToString(),
            account.LibraryScopeId
        });
        await context.SaveChangesAsync(cancellationToken);
        return Ok(AccountResponse(account, null, scope == ProviderAccountScope.User ? session.UserName : null));
    }

    private bool TryGetSession(
        out AdminAuthSession session,
        out IActionResult? error)
    {
        session = null!;
        error = null;
        if (HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) &&
            value is AdminAuthSession current)
        {
            session = current;
            return true;
        }

        error = Unauthorized(new { error = "Authentication required" });
        return false;
    }

    private static bool TryNormalizeRequest(
        CreateProviderAccountRequest request,
        AdminAuthSession session,
        bool canManageAllAccounts,
        out NormalizedAccountRequest normalized,
        out string? error)
    {
        normalized = default;
        error = null;
        var providerId = request.ProviderId?.Trim().ToLowerInvariant();
        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(providerId) || !ProviderIdPattern().IsMatch(providerId))
        {
            error = "ProviderId must use lowercase letters, numbers, and single hyphens";
            return false;
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200)
        {
            error = "DisplayName is required and must be at most 200 characters";
            return false;
        }

        if (!Enum.TryParse<ProviderAccountScope>(request.Scope, ignoreCase: true, out var scope))
        {
            error = "Scope must be Global, User, or Library";
            return false;
        }

        Guid? tenantId;
        Guid? ownerUserId;
        string? libraryScopeId = request.LibraryScopeId?.Trim();
        if (canManageAllAccounts)
        {
            tenantId = scope == ProviderAccountScope.Global ? null : request.TenantId ?? session.TenantId;
            ownerUserId = scope == ProviderAccountScope.User ? request.OwnerUserId ?? session.AllstarrUserId : null;
        }
        else
        {
            if (scope != ProviderAccountScope.User ||
                !session.TenantId.HasValue ||
                !session.AllstarrUserId.HasValue)
            {
                error = "Users may create only their own user-scoped accounts";
                return false;
            }

            tenantId = session.TenantId;
            ownerUserId = session.AllstarrUserId;
            libraryScopeId = null;
        }

        if (scope is ProviderAccountScope.Global or ProviderAccountScope.User)
        {
            libraryScopeId = null;
        }

        if (scope != ProviderAccountScope.Global && !tenantId.HasValue)
        {
            error = "TenantId is required for non-global accounts";
            return false;
        }

        if (scope == ProviderAccountScope.User && !ownerUserId.HasValue)
        {
            error = "OwnerUserId is required for user accounts";
            return false;
        }

        if (scope == ProviderAccountScope.Library && string.IsNullOrWhiteSpace(libraryScopeId))
        {
            error = "LibraryScopeId is required for library accounts";
            return false;
        }

        normalized = new NormalizedAccountRequest(
            providerId,
            displayName,
            scope,
            tenantId,
            ownerUserId,
            libraryScopeId);
        return true;
    }

    private async Task<string?> ValidateAccountScopeAsync(
        NormalizedAccountRequest request,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (request.Scope == ProviderAccountScope.Global)
        {
            return request.TenantId == null &&
                   request.OwnerUserId == null &&
                   request.LibraryScopeId == null
                ? null
                : "Global accounts cannot have a tenant, owner, or library scope";
        }

        if (!request.TenantId.HasValue ||
            !await context.Tenants.AsNoTracking().AnyAsync(
                item => item.Id == request.TenantId.Value,
                cancellationToken))
        {
            return "The selected tenant does not exist";
        }

        if (request.Scope == ProviderAccountScope.Library)
        {
            return request.OwnerUserId == null && !string.IsNullOrWhiteSpace(request.LibraryScopeId)
                ? null
                : "Library accounts require a library scope and cannot have a user owner";
        }

        if (!request.OwnerUserId.HasValue)
        {
            return "User accounts require an owner";
        }

        var ownerIsActiveInTenant = await context.Users.AsNoTracking().AnyAsync(
            item => item.Id == request.OwnerUserId.Value &&
                    item.TenantId == request.TenantId.Value &&
                    item.Status == PlatformUserStatus.Active,
            cancellationToken);
        return ownerIsActiveInTenant
            ? null
            : "The selected account owner is inactive or belongs to another tenant";
    }

    private bool CanAccessManagement(AdminAuthSession session) =>
        _managementMode != ProviderAccountManagementMode.AdminManaged || session.IsAdministrator;

    private bool CanManageAllAccounts(AdminAuthSession session) =>
        session.IsAdministrator &&
        _managementMode is ProviderAccountManagementMode.AdminManaged or ProviderAccountManagementMode.Hybrid;

    private IActionResult? GetManagementAccessError(AdminAuthSession session)
    {
        if (!CanAccessManagement(session))
        {
            return ManagementForbidden();
        }

        if (!CanManageAllAccounts(session) &&
            (!session.TenantId.HasValue || !session.AllstarrUserId.HasValue))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "The backend identity is not linked to an Allstarr user."
            });
        }

        return null;
    }

    private IQueryable<ProviderAccountRecord> ApplyManagementScope(
        IQueryable<ProviderAccountRecord> query,
        AdminAuthSession session) => CanManageAllAccounts(session)
            ? query
            : query.Where(item =>
                item.Scope == ProviderAccountScope.User &&
                item.TenantId == session.TenantId &&
                item.OwnerUserId == session.AllstarrUserId);

    private IActionResult ManagementForbidden() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "Provider account management is restricted to administrators."
        });

    private void AddAudit(
        AllstarrDbContext context,
        AdminAuthSession session,
        string action,
        string outcome,
        object details)
    {
        var correlationId = HttpContext.Items[CorrelationMiddleware.HttpContextItemKey]?.ToString()
                            ?? HttpContext.TraceIdentifier;
        context.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = session.TenantId,
            ActorUserId = session.AllstarrUserId,
            Category = "provider-account",
            Action = action,
            Outcome = outcome,
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static object AccountResponse(
        ProviderAccountRecord account,
        SecretReferenceRecord? secret,
        string? ownerDisplayName = null,
        string? creatorDisplayName = null) => new
        {
            account.Id,
            account.ProviderId,
            displayName = FriendlyDisplayName(account),
            sourceDisplayName = SourceDisplayName(account, creatorDisplayName),
            scope = account.Scope.ToString(),
            account.TenantId,
            account.OwnerUserId,
            ownerDisplayName,
            account.CreatedByUserId,
            creatorDisplayName,
            account.LibraryScopeId,
            account.Enabled,
            account.Revision,
            secret = new
            {
                configured = account.SecretReferenceId.HasValue,
                version = secret?.ActiveVersion,
                updatedAt = secret?.UpdatedAt,
                revoked = secret?.RevokedAt.HasValue ?? false
            },
            account.CreatedAt,
            account.UpdatedAt
        };

    private static string SourceDisplayName(ProviderAccountRecord account, string? creatorDisplayName)
    {
        var name = FriendlyDisplayName(account);
        return string.IsNullOrWhiteSpace(creatorDisplayName)
            ? name
            : $"{name} · {creatorDisplayName}";
    }

    private static string FriendlyDisplayName(ProviderAccountRecord account)
    {
        if (account.DisplayName is not ("Legacy .env import" or "Legacy .env import (current user)"))
        {
            return account.DisplayName;
        }

        var provider = account.ProviderId.ToLowerInvariant() switch
        {
            "lastfm" => "Last.fm",
            "listenbrainz" => "ListenBrainz",
            "qobuz" => "Qobuz",
            "deezer" => "Deezer",
            "spotify" => "Spotify",
            _ => account.ProviderId
        };
        return account.Scope == ProviderAccountScope.User
            ? $"My {provider} account"
            : $"Shared {provider} account";
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdPattern();

    public sealed class CreateProviderAccountRequest
    {
        public string? ProviderId { get; set; }
        public string? DisplayName { get; set; }
        public string Scope { get; set; } = nameof(ProviderAccountScope.User);
        public Guid? TenantId { get; set; }
        public Guid? OwnerUserId { get; set; }
        public string? LibraryScopeId { get; set; }
        public bool Enabled { get; set; } = true;
        public JsonElement? Secret { get; set; }
    }

    public sealed class SetProviderAccountEnabledRequest
    {
        public bool Enabled { get; set; }
        public long? ExpectedRevision { get; set; }
    }

    public sealed class UpdateProviderAccountAudienceRequest
    {
        public string Scope { get; set; } = nameof(ProviderAccountScope.User);
        public string? LibraryScopeId { get; set; }
        public long? ExpectedRevision { get; set; }
    }

    public sealed class ReplaceProviderSecretRequest
    {
        public JsonElement Secret { get; set; }
    }

    private readonly record struct NormalizedAccountRequest(
        string ProviderId,
        string DisplayName,
        ProviderAccountScope Scope,
        Guid? TenantId,
        Guid? OwnerUserId,
        string? LibraryScopeId);
}
