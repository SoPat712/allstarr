using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using allstarr.Core.ClientApi;

namespace allstarr.Middleware;

public sealed class AllstarrCorsPolicyProvider(IOptions<CorsOptions> options, ClientApiAvailability availability) : ICorsPolicyProvider
{
    private readonly DefaultCorsPolicyProvider _defaults = new(options);

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var policy = await _defaults.GetPolicyAsync(context, policyName);
        if (policy == null || !await availability.IsEnabledAsync(context)) return policy;
        var builder = new CorsPolicyBuilder(policy).WithExposedHeaders("X-Allstarr-Playback-Request");
        if (policy.AllowAnyHeader) return builder.Build();
        var headers = context.Request.Headers.AccessControlRequestHeaders.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(header => header.StartsWith("X-Allstarr-", StringComparison.OrdinalIgnoreCase) ||
                header.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase)).ToArray();
        return builder.WithHeaders(headers.Except(policy.Headers, StringComparer.OrdinalIgnoreCase).ToArray()).Build();
    }
}
