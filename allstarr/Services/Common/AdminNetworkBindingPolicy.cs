using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Sockets;

namespace allstarr.Services.Common;

public static class AdminNetworkBindingPolicy
{
    private const string BindAnyIpKey = "Admin:BindAnyIp";
    private const string ContainerizedKey = "Admin:Containerized";
    private const string ContainerGatewayKey = "Admin:ContainerGateway";
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
    /// Container listeners must bind to the container interface so a host-published
    /// loopback port can reach them. The request allowlist remains a separate check.
    /// </summary>
    public static bool ShouldListenAdminAnyIp(IConfiguration configuration)
    {
        return ShouldBindAdminAnyIp(configuration) || configuration.GetValue<bool>(ContainerizedKey);
    }

    public static IReadOnlySet<IPAddress> ResolveContainerGateways(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(ContainerizedKey) || ShouldBindAdminAnyIp(configuration))
        {
            return new HashSet<IPAddress>();
        }

        var configured = configuration.GetValue<string>(ContainerGatewayKey);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new HashSet<IPAddress>();
        }

        var addresses = new HashSet<IPAddress>();
        foreach (var entry in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var gateway in ReadLinuxDefaultGateways())
                {
                    addresses.Add(gateway);
                }
                continue;
            }

            if (IPAddress.TryParse(entry, out var address))
            {
                addresses.Add(NormalizeAddress(address));
                continue;
            }

            try
            {
                foreach (var resolved in Dns.GetHostAddresses(entry))
                {
                    addresses.Add(NormalizeAddress(resolved));
                }
            }
            catch (SocketException)
            {
                // A missing gateway name keeps the admin surface closed.
            }
        }

        return addresses;
    }

    public static IReadOnlySet<IPAddress> ParseLinuxDefaultGateways(IEnumerable<string> routeLines)
    {
        var gateways = new HashSet<IPAddress>();
        foreach (var line in routeLines.Skip(1))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 || columns[1] != "00000000" ||
                !uint.TryParse(columns[2], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var encodedGateway))
            {
                continue;
            }

            var bytes = BitConverter.GetBytes(encodedGateway);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            gateways.Add(new IPAddress(bytes));
        }

        return gateways;
    }

    private static IReadOnlySet<IPAddress> ReadLinuxDefaultGateways()
    {
        const string routeFile = "/proc/net/route";
        try
        {
            return File.Exists(routeFile)
                ? ParseLinuxDefaultGateways(File.ReadLines(routeFile))
                : new HashSet<IPAddress>();
        }
        catch (IOException)
        {
            return new HashSet<IPAddress>();
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<IPAddress>();
        }
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

    public static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
