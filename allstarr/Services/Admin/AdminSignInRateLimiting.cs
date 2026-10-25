using System.Globalization;
using System.Threading.RateLimiting;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Services.Admin;

public static class AdminSignInRateLimiting
{
    public const string PolicyName = "admin-sign-in";

    private const int PerAddressPermitLimit = 20;
    private const int GlobalPermitLimit = 60;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const string UnknownAddressPartition = "unknown";
    private const string UnrestrictedPartition = "unrestricted";
    private const string GlobalPartition = "all-admin-sign-ins";
    private const string RejectionMessage = "Too many sign-in attempts. Try again later.";

    public static IServiceCollection AddAdminSignInRateLimiting(this IServiceCollection services) =>
        services.AddAdminSignInRateLimiting(Window, PerAddressPermitLimit, GlobalPermitLimit);

    internal static IServiceCollection AddAdminSignInRateLimiting(
        this IServiceCollection services,
        TimeSpan window,
        int perAddressPermitLimit,
        int globalPermitLimit)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        if (perAddressPermitLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(perAddressPermitLimit));
        if (globalPermitLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(globalPermitLimit));

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsSignInEndpoint(context)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        GlobalPartition,
                        _ => CreateFixedWindowOptions(window, globalPermitLimit))
                    : RateLimitPartition.GetNoLimiter(UnrestrictedPartition));

            options.AddPolicy(PolicyName, context =>
                IsSignInEndpoint(context)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        AddressPartition(context),
                        _ => CreateFixedWindowOptions(window, perAddressPermitLimit))
                    : RateLimitPartition.GetNoLimiter(UnrestrictedPartition));

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;
        });

        return services;
    }

    private static bool IsSignInEndpoint(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (!string.Equals(endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName,
                PolicyName, StringComparison.Ordinal)) return false;

        // The proxy also handles normalized sign-in paths outside the dedicated action.
        return endpoint!.Metadata.GetMetadata<NativeSignInFallbackAttribute>() is null ||
               HttpMethods.IsPost(context.Request.Method) &&
               string.Equals(context.Request.Path.Value?.Trim().Trim('/'),
                   "Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase);
    }

    private static string AddressPartition(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is null
            ? UnknownAddressPartition
            : AdminNetworkBindingPolicy.NormalizeAddress(address).ToString();
    }

    private static FixedWindowRateLimiterOptions CreateFixedWindowOptions(
        TimeSpan window,
        int permitLimit) =>
        new()
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        };

    private static async ValueTask WriteRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers["Retry-After"] = RetryAfterSeconds(context.Lease);

        try
        {
            await response.WriteAsJsonAsync(
                new { error = RejectionMessage },
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The rate-limit middleware still disposes its leases when a client disconnects.
        }
    }

    private static string RetryAfterSeconds(RateLimitLease lease)
    {
        var seconds = 60L;
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter) &&
            retryAfter > TimeSpan.Zero)
        {
            seconds = Math.Max(1L, (long)Math.Ceiling(retryAfter.TotalSeconds));
        }

        return seconds.ToString(CultureInfo.InvariantCulture);
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class NativeSignInFallbackAttribute : Attribute { }
