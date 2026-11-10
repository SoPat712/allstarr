using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using System.Text;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace allstarr.Services.Admin;

public sealed class AdminOidcOptions
{
    public const string Scheme = "admin-oidc";
    public const string PendingScheme = "admin-oidc-pending";
    public const string CallbackPath = "/api/admin/auth/oidc/callback";
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public string DisplayName { get; set; } = "Single sign-on";

    internal bool AllowsHttp => Enabled && IsAllowedUrl(PublicUrl, out var url) && url!.Scheme == "http";

    internal bool IsRequestAllowed(HttpRequest request) =>
        request.HttpContext.Connection.LocalPort == 5275 && (request.IsHttps || (AllowsHttp && request.Scheme == "http"));

    public void Validate(string basePath)
    {
        if (!Enabled) return;
        if (!IsAllowedUrl(Authority, out _) || !IsAllowedUrl(PublicUrl, out var url) ||
            url!.AbsolutePath.TrimEnd('/') != basePath || string.IsNullOrWhiteSpace(ClientId) ||
            string.IsNullOrWhiteSpace(ClientSecret) || string.IsNullOrWhiteSpace(DisplayName))
            throw new InvalidOperationException(
                "Admin OIDC requires HTTPS URLs or HTTP URLs with private/local hosts, ClientId, ClientSecret and DisplayName. " +
                "PublicUrl must end at Admin:BasePath, without query or fragment.");
        PublicUrl = PublicUrl.TrimEnd('/');
    }

    internal static bool IsAllowedUrl(string value, out Uri? url) =>
        Uri.TryCreate(value, UriKind.Absolute, out url) &&
        (url.Scheme == "https" || (url.Scheme == "http" && IsPrivateHost(url.IdnHost))) &&
        string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment);

    private static bool IsPrivateHost(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.Equals(IPAddress.IPv6Loopback)) return true;
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return bytes[0] is 127 or 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254) ||
                    (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
            return (bytes[0] & 0xfe) == 0xfc || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
        }
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }
}

public static class AdminOidcRegistration
{
    public static IServiceCollection AddAdminOidc(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Admin:Oidc").Get<AdminOidcOptions>() ?? new();
        var basePath = new AdminBasePath(configuration).Value;
        settings.Validate(basePath);
        services.AddSingleton(settings);
        services.AddSingleton<AdminOidcLinks>();
        services.AddSingleton<AdminOidcBackendAuthentication>();
        services.AddHttpClient(AdminOidcBackendAuthentication.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = 256 * 1024;
        }).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-Allstarr-CSRF";
            options.Cookie.Name = "allstarr_admin_csrf";
            options.Cookie.Path = string.IsNullOrEmpty(basePath) ? "/" : basePath;
            options.Cookie.SameSite = settings.AllowsHttp ? SameSiteMode.Lax : SameSiteMode.Strict;
            options.Cookie.SecurePolicy = settings.Enabled && !settings.AllowsHttp ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        var authentication = services.AddAuthentication();
        if (!settings.Enabled) return services;

        authentication.AddCookie(AdminOidcOptions.PendingScheme, options =>
        {
            options.Cookie.Name = "allstarr_oidc_pending";
            options.Cookie.Path = string.IsNullOrEmpty(basePath) ? "/" : basePath;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = settings.AllowsHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            options.SlidingExpiration = false;
        }).AddOpenIdConnect(AdminOidcOptions.Scheme, options =>
        {
            options.SignInScheme = AdminOidcOptions.PendingScheme;
            options.Authority = settings.Authority;
            options.RequireHttpsMetadata = new Uri(settings.Authority).Scheme == "https";
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.CallbackPath = AdminOidcOptions.CallbackPath;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            // Providers without a signing certificate (Authentik with no signing key, for one) sign ID tokens with the client
            // secret (HS256) and publish no keys; the handler adds the provider's published keys to this one.
            options.TokenValidationParameters.IssuerSigningKeys = [new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.ClientSecret))];
            options.MapInboundClaims = false;
            options.ClaimActions.Clear();
            options.SaveTokens = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.CorrelationCookie.Path = basePath + AdminOidcOptions.CallbackPath;
            options.NonceCookie.Path = basePath + AdminOidcOptions.CallbackPath;
            if (settings.AllowsHttp)
            {
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
            }
            options.Events = new OpenIdConnectEvents
            {
                OnMessageReceived = context =>
                {
                    if (context.HttpContext.Connection.LocalPort != 5275) context.SkipHandler();
                    else if (!settings.IsRequestAllowed(context.Request)) context.Fail("OIDC requires HTTPS unless the public address uses private/local HTTP.");
                    return Task.CompletedTask;
                },
                OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = settings.PublicUrl + AdminOidcOptions.CallbackPath;
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    context.Properties ??= new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
                    context.Properties.IssuedUtc = DateTimeOffset.UtcNow;
                    context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                    var subject = context.Principal?.FindFirst("sub")?.Value;
                    if (string.IsNullOrEmpty(subject)) context.Fail("Missing subject.");
                    else context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("iss", context.SecurityToken.Issuer), new Claim("sub", subject)], AdminOidcOptions.Scheme));
                    return Task.CompletedTask;
                },
                OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect(settings.PublicUrl + "/?oidc=failed");
                    return Task.CompletedTask;
                }
            };
        });
        return services;
    }
}
