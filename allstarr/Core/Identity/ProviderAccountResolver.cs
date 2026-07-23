using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Identity;

public sealed class ProviderPolicyOptions
{
    public const string SectionName = "ProviderPolicy";

    public bool AllowGlobalAccounts { get; set; } = true;
    public bool AllowGlobalPersonalAccounts { get; set; }
    public Guid? SharedDownloaderAccountId { get; set; }
}

public sealed record ProviderAccountResolutionRequest(
    AllstarrPrincipal Principal,
    string ProviderId,
    string Capability,
    Guid? RequestedAccountId = null,
    string? LibraryScopeId = null);

public sealed record ResolvedProviderAccount(
    ProviderAccountRecord Account,
    string Reason);

public sealed class ProviderAccountResolver
{
    private static readonly IReadOnlySet<string> PersonalCapabilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "playlist",
            "personal-library",
            "scrobbling",
            "favorites"
        };

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly ProviderPolicyOptions _policy;

    public ProviderAccountResolver(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        ProviderPolicyOptions policy)
    {
        _contextFactory = contextFactory;
        _policy = policy;
    }

    public async Task<ResolvedProviderAccount?> ResolveAsync(
        ProviderAccountResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderId) ||
            string.IsNullOrWhiteSpace(request.Capability))
        {
            throw new ArgumentException("Provider and capability are required.", nameof(request));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .Where(item => item.Enabled && item.ProviderId == request.ProviderId.Trim().ToLowerInvariant())
            .ToListAsync(cancellationToken);
        var eligible = accounts
            .Where(account => IsEligible(account, request))
            .ToList();

        if (request.RequestedAccountId.HasValue)
        {
            var requested = eligible.SingleOrDefault(item => item.Id == request.RequestedAccountId.Value);
            if (requested == null)
            {
                throw new UnauthorizedAccessException(
                    "The requested provider account is outside the caller scope or policy.");
            }

            return new ResolvedProviderAccount(requested, "explicit_account");
        }

        if (request.Capability.Equals("download", StringComparison.OrdinalIgnoreCase) &&
            _policy.SharedDownloaderAccountId.HasValue)
        {
            var shared = eligible.SingleOrDefault(item => item.Id == _policy.SharedDownloaderAccountId.Value);
            if (shared != null)
            {
                return new ResolvedProviderAccount(shared, "policy_shared_downloader");
            }
        }

        var user = eligible.FirstOrDefault(item =>
            item.Scope == ProviderAccountScope.User &&
            item.OwnerUserId == request.Principal.UserId);
        if (user != null)
        {
            return new ResolvedProviderAccount(user, "user_account");
        }

        var library = eligible.FirstOrDefault(item => item.Scope == ProviderAccountScope.Library);
        if (library != null)
        {
            return new ResolvedProviderAccount(library, "library_account");
        }

        var global = eligible.FirstOrDefault(item => item.Scope == ProviderAccountScope.Global);
        return global == null ? null : new ResolvedProviderAccount(global, "global_account");
    }

    private bool IsEligible(
        ProviderAccountRecord account,
        ProviderAccountResolutionRequest request)
    {
        return account.Scope switch
        {
            ProviderAccountScope.User =>
                account.TenantId == request.Principal.TenantId &&
                account.OwnerUserId == request.Principal.UserId,
            ProviderAccountScope.Library =>
                account.TenantId == request.Principal.TenantId &&
                !string.IsNullOrWhiteSpace(request.LibraryScopeId) &&
                account.LibraryScopeId == request.LibraryScopeId,
            ProviderAccountScope.Global =>
                account.TenantId == null &&
                _policy.AllowGlobalAccounts &&
                (!PersonalCapabilities.Contains(request.Capability) ||
                 _policy.AllowGlobalPersonalAccounts ||
                 request.Principal.IsAdministrator &&
                 request.RequestedAccountId == account.Id),
            _ => false
        };
    }
}
