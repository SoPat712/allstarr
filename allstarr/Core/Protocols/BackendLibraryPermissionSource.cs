using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Subsonic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Protocols;

public sealed class BackendLibraryPermissionSource(
    IHttpContextAccessor httpContexts,
    IDbContextFactory<AllstarrDbContext> factory,
    EncryptedSecretStore secrets,
    IOptions<JellyfinSettings> jellyfin,
    IOptions<SubsonicSettings> subsonic) : IBackendLibraryPermissionSource
{
    private static readonly HashSet<string> SubsonicAuthenticationKeys = new(StringComparer.OrdinalIgnoreCase)
        { "u", "p", "t", "s", "apiKey", "v", "c" };

    public async Task<HttpRequestMessage?> CreateRequestAsync(ProtocolExecutionContext context, CancellationToken cancellationToken)
    {
        var principal = context.Principal;
        if (principal == null) return null;
        var http = httpContexts.HttpContext;
        if (http != null)
        {
            var current = http.GetProtocolExecutionContext();
            if (current?.Principal?.UserId == principal.UserId && current.Principal.TenantId == principal.TenantId &&
                current.Protocol == context.Protocol && current.BackendInstanceId == context.BackendInstanceId &&
                current.VerifiedBackendPrincipalId == context.VerifiedBackendPrincipalId)
            {
                if (context.Protocol == ProtocolKind.Jellyfin)
                {
                    var token = AuthHeaderHelper.ExtractToken(http.Request.Headers);
                    token ??= http.Request.Query["api_key"].FirstOrDefault()
                        ?? http.Request.Query["ApiKey"].FirstOrDefault();
                    return string.IsNullOrWhiteSpace(token) ? null : Jellyfin(context, token);
                }
                var parameters = http.Items[SubsonicAuthFilter.RequestParametersItemKey] as SubsonicRequestParameters;
                return parameters == null ? null : Subsonic(parameters.Select(SubsonicAuthenticationKeys));
            }
            if (current != null) return null;

            if (http.Items[AdminAuthSessionService.HttpContextSessionItemKey] is AdminAuthSession session &&
                session.AllstarrUserId == principal.UserId && session.TenantId == principal.TenantId &&
                session.UserId == context.VerifiedBackendPrincipalId &&
                session.BackendType.Equals(context.Protocol.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                if (context.Protocol == ProtocolKind.Jellyfin)
                    return string.IsNullOrWhiteSpace(session.JellyfinAccessToken) ? null : Jellyfin(context, session.JellyfinAccessToken);
                return session.SubsonicReadAuthentication == null ? null :
                    Subsonic(SubsonicRequestParameters.FromDictionary(session.SubsonicReadAuthentication));
            }
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var identity = await (from binding in db.BackendIdentities.AsNoTracking()
                              join user in db.Users on binding.UserId equals user.Id
                              where binding.TenantId == principal.TenantId && binding.UserId == principal.UserId &&
                                    binding.BackendType == principal.BackendType &&
                                    binding.BackendInstanceId == context.BackendInstanceId &&
                                    binding.PrincipalId == context.VerifiedBackendPrincipalId &&
                                    user.TenantId == principal.TenantId && user.Status == PlatformUserStatus.Active
                              select binding).SingleOrDefaultAsync(cancellationToken);
        if (identity == null) return null;
        if (context.Protocol == ProtocolKind.Jellyfin)
            return string.IsNullOrWhiteSpace(jellyfin.Value.ApiKey) ? null : Jellyfin(context, jellyfin.Value.ApiKey);

        var reference = await db.SecretReferences.AsNoTracking().Where(item =>
                item.TenantId == identity.TenantId && item.BackendIdentityId == identity.Id &&
                item.Purpose == BackendCredentialScope.SubsonicPurpose && item.RevokedAt == null)
            .OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (reference == null) return null;
        using var lease = await secrets.OpenAsync(reference.Id, new(identity.TenantId), cancellationToken);
        using var credential = JsonDocument.Parse(lease.Value);
        var root = credential.RootElement;
        if (!root.TryGetProperty("username", out var username) || username.ValueKind != JsonValueKind.String ||
            username.GetString() != identity.PrincipalId ||
            !root.TryGetProperty("password", out var password) || password.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(password.GetString())) return null;
        return Subsonic(SubsonicRequestParameters.FromDictionary(new Dictionary<string, string>
        {
            ["u"] = username.GetString()!,
            ["p"] = password.GetString()!,
            ["v"] = "1.16.1",
            ["c"] = "allstarr"
        }));
    }

    private HttpRequestMessage? Jellyfin(ProtocolExecutionContext context, string token)
    {
        if (string.IsNullOrWhiteSpace(jellyfin.Value.Url)) return null;
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(jellyfin.Value.Url.TrimEnd('/') + "/"),
            "UserViews?UserId=" + Uri.EscapeDataString(context.VerifiedBackendPrincipalId) + "&IncludeExternalContent=false&IncludeHidden=true"));
        request.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(token));
        return request;
    }

    private HttpRequestMessage? Subsonic(SubsonicRequestParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(subsonic.Value.Url)) return null;
        return new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(subsonic.Value.Url.TrimEnd('/') + "/"), "rest/getMusicFolders.view"))
        {
            Content = new FormUrlEncodedContent(parameters.Select(SubsonicAuthenticationKeys).SetValue("f", "json"))
        };
    }
}
