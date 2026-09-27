using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Middleware;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace allstarr.Tests;

public sealed class AdminOidcTests
{
    [Theory]
    [InlineData("https://admin.test/callback?code=private-code&state=private-state")]
    [InlineData("?code=private-code&state=private-state")]
    [InlineData("nonce=private-nonce")]
    [InlineData("code_verifier=private-verifier")]
    [InlineData("client_secret=private-client-secret")]
    public void CallbackSecretsAreRedacted(string value)
    {
        var sanitized = allstarr.Core.Operations.SafeOperationalText.Sanitize(value);
        Assert.DoesNotContain("private-", sanitized);
    }

    [Theory]
    [InlineData("http://idp.test", "https://admin.test/allstarr")]
    [InlineData("https://idp.test", "https://admin.test/wrong")]
    [InlineData("https://idp.test", "https://admin.test/allstarr?redirect=elsewhere")]
    [InlineData("https://idp.test", "https://user@admin.test/allstarr")]
    public void InvalidConfigurationFailsClosed(string authority, string publicUrl)
    {
        var options = new AdminOidcOptions
        {
            Enabled = true,
            Authority = authority,
            PublicUrl = publicUrl,
            ClientId = "app",
            ClientSecret = "fixture"
        };
        Assert.Throws<InvalidOperationException>(() => options.Validate("/allstarr"));
    }

    [Fact]
    public void DisabledOidcDoesNotRequireConfiguration() => new AdminOidcOptions().Validate("");

    [Fact]
    public void IdentityUsesIssuerSubjectAndClient_NotEmailOrRoles()
    {
        static ClaimsPrincipal Identity(string issuer, string subject, string email) => new(new ClaimsIdentity(
            [new("iss", issuer), new("sub", subject), new("email", email), new(ClaimTypes.Role, "admin")], "oidc"));
        var key = AdminOidcLinks.IdentityKey(Identity("one", "alice", "same@test"), "app");
        Assert.Equal(key, AdminOidcLinks.IdentityKey(Identity("one", "alice", "changed@test"), "app"));
        Assert.NotEqual(key, AdminOidcLinks.IdentityKey(Identity("two", "alice", "same@test"), "app"));
        Assert.NotEqual(key, AdminOidcLinks.IdentityKey(Identity("one", "bob", "same@test"), "app"));
        Assert.NotEqual(key, AdminOidcLinks.IdentityKey(Identity("one", "alice", "same@test"), "other-app"));
        Assert.Null(AdminOidcLinks.IdentityKey(new ClaimsPrincipal(new ClaimsIdentity([new("iss", "one"), new("sub", "alice")])), "app"));
    }

    [Theory]
    [InlineData("", "none")]
    [InlineData("/allstarr", "none")]
    [InlineData("/allstarr", "signature")]
    [InlineData("/allstarr", "issuer")]
    [InlineData("/allstarr", "audience")]
    [InlineData("/allstarr", "nonce")]
    [InlineData("/allstarr", "state")]
    [InlineData("/allstarr", "cookie")]
    public async Task AuthorizationCodeFlowVerifiesTokenAndPreservesPrefix(string prefix, string failure)
    {
        using var signingRsa = RSA.Create(2048);
        using var wrongRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(signingRsa) { KeyId = "fixture" };
        var tokenKey = failure == "signature" ? new RsaSecurityKey(wrongRsa) { KeyId = "fixture" } : key;
        string? nonce = null;
        var backchannel = new TokenHandler(() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = failure == "issuer" ? "https://wrong-issuer.test" : "https://idp.test",
            Audience = failure == "audience" ? "other-application" : "allstarr-test",
            Expires = DateTime.UtcNow.AddMinutes(5),
            IssuedAt = DateTime.UtcNow,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "subject-1",
                ["nonce"] = failure == "nonce" ? "wrong-nonce" : nonce!,
                ["email"] = "not-an-identity@test",
                ["role"] = "admin"
            },
            SigningCredentials = new SigningCredentials(tokenKey, SecurityAlgorithms.RsaSha256)
        }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Admin:BasePath"] = prefix,
            ["Admin:Oidc:Enabled"] = "true",
            ["Admin:Oidc:Authority"] = "https://idp.test",
            ["Admin:Oidc:PublicUrl"] = "https://admin.test" + prefix,
            ["Admin:Oidc:ClientId"] = "allstarr-test",
            ["Admin:Oidc:ClientSecret"] = "fixture-secret"
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<AdminBasePath>();
            services.AddAdminOidc(config);
            services.Configure<OpenIdConnectOptions>(AdminOidcOptions.Scheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://idp.test",
                    AuthorizationEndpoint = "https://idp.test/authorize",
                    TokenEndpoint = "https://idp.test/token"
                };
                options.Configuration.SigningKeys.Add(key);
                options.Backchannel = new HttpClient(backchannel);
            });
        }).Configure(app =>
        {
            app.Use((context, next) =>
            {
                context.Connection.LocalPort = context.Request.Headers.ContainsKey("X-Test-Native-Port") ? 8080 : 5275;
                return next(context);
            });
            app.UseMiddleware<AdminBasePathMiddleware>();
            app.UseAuthentication();
            app.Run(async context =>
            {
                if (context.Connection.LocalPort == 8080) await context.Response.WriteAsync("native");
                else if (context.Request.Path == "/login")
                    await context.ChallengeAsync(AdminOidcOptions.Scheme, new AuthenticationProperties { RedirectUri = prefix + "/complete" });
                else
                {
                    var pending = await context.AuthenticateAsync(AdminOidcOptions.PendingScheme);
                    await context.Response.WriteAsJsonAsync(new
                    {
                        authenticated = pending.Succeeded,
                        claims = pending.Principal?.Claims.Select(claim => claim.Type).ToArray(),
                        tokens = pending.Properties?.GetTokens().Count(),
                        expires = pending.Properties?.ExpiresUtc
                    });
                }
            });
        })).StartAsync();
        using var client = host.GetTestClient();
        client.BaseAddress = new Uri("https://untrusted-host.test");
        using var nativeRequest = new HttpRequestMessage(HttpMethod.Get, AdminOidcOptions.CallbackPath + "?code=ignored&state=ignored");
        nativeRequest.Headers.Add("X-Test-Native-Port", "true");
        using var nativeResponse = await client.SendAsync(nativeRequest);
        Assert.Equal(HttpStatusCode.OK, nativeResponse.StatusCode);
        Assert.Equal("native", await nativeResponse.Content.ReadAsStringAsync());
        Assert.False(nativeResponse.Headers.Contains("Set-Cookie"));
        using var challenge = await client.GetAsync(prefix + "/login");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var parameters = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        Assert.Equal("code", parameters["response_type"]);
        Assert.Equal("S256", parameters["code_challenge_method"]);
        Assert.Equal("https://admin.test" + prefix + AdminOidcOptions.CallbackPath, parameters["redirect_uri"]);
        nonce = parameters["nonce"];
        var cookies = challenge.Headers.GetValues("Set-Cookie").ToArray();
        Assert.All(cookies, cookie => Assert.Contains("path=" + prefix + AdminOidcOptions.CallbackPath, cookie));
        using var callback = new HttpRequestMessage(HttpMethod.Get,
            QueryHelpers.AddQueryString(prefix + AdminOidcOptions.CallbackPath, new Dictionary<string, string?>
            {
                ["code"] = "fixture-code",
                ["state"] = failure == "state" ? "invalid-state" : parameters["state"]
            }));
        if (failure != "cookie") callback.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => cookie.Split(';')[0])));
        using var response = await client.SendAsync(callback);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        if (failure != "none")
        {
            Assert.EndsWith("/?oidc=failed", response.Headers.Location!.ToString());
            Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var failedCookies) ? failedCookies : [],
                cookie => cookie.StartsWith("allstarr_oidc_pending="));
            return;
        }
        Assert.Equal(prefix + "/complete", response.Headers.Location!.ToString());
        var pendingCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("allstarr_oidc_pending="));
        Assert.Contains("secure", pendingCookie);
        Assert.Contains("httponly", pendingCookie);
        using var complete = new HttpRequestMessage(HttpMethod.Get, prefix + "/complete");
        complete.Headers.Add("Cookie", pendingCookie.Split(';')[0]);
        using var completeResponse = await client.SendAsync(complete);
        using var result = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(new[] { "iss", "sub" }, result.RootElement.GetProperty("claims").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(0, result.RootElement.GetProperty("tokens").GetInt32());
        Assert.InRange(result.RootElement.GetProperty("expires").GetDateTimeOffset(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Contains("code_verifier=", backchannel.RequestBody);
        Assert.Equal("https://admin.test" + prefix + AdminOidcOptions.CallbackPath, QueryHelpers.ParseQuery(backchannel.RequestBody)["redirect_uri"]);
    }

    private sealed class TokenHandler(Func<string> token) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { access_token = "unused", token_type = "Bearer", id_token = token() }), Encoding.UTF8, "application/json")
            };
        }
    }
}
