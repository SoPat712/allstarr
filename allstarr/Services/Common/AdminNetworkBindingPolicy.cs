using Microsoft.Extensions.Configuration;
using System.Net;

namespace allstarr.Services.Common;

public static class AdminNetworkBindingPolicy
{
    private const string BindAnyIpKey = "Admin:BindAnyIp";
    private const string TrustedSubnetsKey = "Admin:TrustedSubnets";

    /// <summary>
    /// Returns whether the admin listener should bind to all interfaces.
    /// Default is false (localhost-only).
    /// </summary>
    public static bool ShouldBindAdminAnyIp(IConfiguration configuration)
    {
        return configuration.GetValue<bool>(BindAnyIpKey);
    }

    /// <summary>
    /// Parses trusted subnet CIDRs from configuration. Format: "192.168.1.0/24,10.0.0.0/8".
    /// </summary>
    public static List<IPNetwork> ParseTrustedSubnets(IConfiguration configuration)
    {
        var raw = configuration.GetValue<string>(TrustedSubnetsKey);
        var networks = new List<IPNetwork>();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return networks;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (IPNetwork.TryParse(part, out var network))
            {
                networks.Add(network);
            }
        }

        return networks;
    }

    /// <summary>
    /// Checks whether a remote IP should be allowed to access the admin listener.
    /// Loopback is always allowed.
    /// </summary>
    public static bool IsRemoteIpAllowed(IPAddress? remoteIp, IReadOnlyCollection<IPNetwork> trustedSubnets)
    {
        if (remoteIp == null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remoteIp))
        {
            return true;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
        {
            remoteIp = remoteIp.MapToIPv4();
        }

        foreach (var subnet in trustedSubnets)
        {
            if (subnet.Contains(remoteIp))
            {
                return true;
            }
        }

        return false;
    }
}
