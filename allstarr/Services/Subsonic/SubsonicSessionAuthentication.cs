using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Services.Admin;

namespace allstarr.Services.Subsonic;

public static class SubsonicSessionAuthentication
{
    private static readonly IReadOnlySet<string> ReadParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "u", "p", "t", "s", "apiKey", "v", "c"
    };

    public static bool TryGetViewerReadParameters(HttpContext httpContext, string backendInstanceId,
        string verifiedPrincipalId, Guid? tenantId, out IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        parameters = [];
        var protocol = httpContext.GetProtocolExecutionContext();
        if (protocol != null)
        {
            if (protocol.Protocol != ProtocolKind.Subsonic ||
                !protocol.BackendInstanceId.Equals(backendInstanceId, StringComparison.Ordinal) ||
                !protocol.VerifiedBackendPrincipalId.Equals(verifiedPrincipalId, StringComparison.Ordinal) ||
                (tenantId.HasValue && protocol.Principal?.TenantId != tenantId) ||
                !httpContext.Items.TryGetValue(SubsonicAuthFilter.RequestParametersItemKey, out var value) ||
                value is not SubsonicRequestParameters requestParameters)
                return false;
            parameters = requestParameters.Ordered
                .Where(parameter => ReadParameterNames.Contains(parameter.Name))
                .Select(parameter => new KeyValuePair<string, string>(parameter.Name, parameter.Value)).ToArray();
            return HasAuthentication(parameters);
        }

        if (!httpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionValue) ||
            sessionValue is not AdminAuthSession session ||
            !IsSubsonicDialect(session.BackendType) ||
            !session.UserId.Equals(verifiedPrincipalId, StringComparison.Ordinal) ||
            (tenantId.HasValue && session.TenantId != tenantId) ||
            session.SubsonicReadAuthentication == null)
            return false;
        parameters = session.SubsonicReadAuthentication.Where(pair => ReadParameterNames.Contains(pair.Key)).ToArray();
        return HasAuthentication(parameters);
    }

    private static bool IsSubsonicDialect(string backendType) =>
        backendType.Equals("subsonic", StringComparison.OrdinalIgnoreCase) ||
        backendType.Equals("navidrome", StringComparison.OrdinalIgnoreCase) ||
        backendType.Equals("opensubsonic", StringComparison.OrdinalIgnoreCase);

    private static bool HasAuthentication(IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        bool Has(string name) => parameters.Any(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pair.Value));
        return Has("apiKey") || (Has("u") && (Has("p") || (Has("t") && Has("s"))));
    }

    public static Dictionary<string, string> Create(string username, string password)
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        // The Subsonic protocol specifies MD5(password + salt) for token authentication.
        var token = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return new Dictionary<string, string>
        {
            ["u"] = username,
            ["t"] = token,
            ["s"] = salt,
            ["v"] = "1.16.1",
            ["c"] = "allstarr-admin"
        };
    }
}
