using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

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

    public void Validate(string basePath)
    {
        if (!Enabled) return;
        if (!IsHttpsUrl(Authority, out _) || !IsHttpsUrl(PublicUrl, out var url) ||
            url!.AbsolutePath.TrimEnd('/') != basePath || string.IsNullOrWhiteSpace(ClientId) ||
            string.IsNullOrWhiteSpace(ClientSecret) || string.IsNullOrWhiteSpace(DisplayName))
            throw new InvalidOperationException(
                "Admin OIDC requires HTTPS Authority and PublicUrl, ClientId, ClientSecret and DisplayName. " +
                "PublicUrl must end at Admin:BasePath, without query or fragment.");
        PublicUrl = PublicUrl.TrimEnd('/');
    }

    private static bool IsHttpsUrl(string value, out Uri? url) =>
        Uri.TryCreate(value, UriKind.Absolute, out url) && url.Scheme == "https" &&
        string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment);
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
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-Allstarr-CSRF";
            options.Cookie.Name = "allstarr_admin_csrf";
            options.Cookie.Path = string.IsNullOrEmpty(basePath) ? "/" : basePath;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = settings.Enabled ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        var authentication = services.AddAuthentication();
        if (!settings.Enabled) return services;

        authentication.AddCookie(AdminOidcOptions.PendingScheme, options =>
        {
            options.Cookie.Name = "allstarr_oidc_pending";
            options.Cookie.Path = string.IsNullOrEmpty(basePath) ? "/" : basePath;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            options.SlidingExpiration = false;
        }).AddOpenIdConnect(AdminOidcOptions.Scheme, options =>
        {
            options.SignInScheme = AdminOidcOptions.PendingScheme;
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.CallbackPath = AdminOidcOptions.CallbackPath;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.ClaimActions.Clear();
            options.SaveTokens = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.CorrelationCookie.Path = basePath + AdminOidcOptions.CallbackPath;
            options.NonceCookie.Path = basePath + AdminOidcOptions.CallbackPath;
            options.Events = new OpenIdConnectEvents
            {
                OnMessageReceived = context =>
                {
                    if (context.HttpContext.Connection.LocalPort != 5275) context.SkipHandler();
                    else if (!context.Request.IsHttps) context.Fail("OIDC requires a trusted HTTPS request.");
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
