using System.Net;
using System.Net.Sockets;

namespace allstarr.Services.Common;

/// <summary>
/// Guards outbound HTTP(S) requests that are derived from external metadata.
/// Blocks local/private targets to reduce SSRF risk.
/// </summary>
public static class OutboundRequestGuard
{
    public static bool TryCreateSafeHttpUri(string? rawUrl, out Uri? safeUri, out string reason)
    {
        safeUri = null;
        reason = "URL is empty";

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsedUri))
        {
            reason = "URL must be absolute";
            return false;
        }

        if (!parsedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Only HTTP/HTTPS URLs are allowed";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedUri.UserInfo))
        {
            reason = "Userinfo in URL is not allowed";
            return false;
        }

        if (parsedUri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(parsedUri.Host, out var ipAddress))
            {
                reason = "Invalid IP address host";
                return false;
            }

            if (!IsPublicRoutableIp(ipAddress))
            {
                reason = "Private/local IP hosts are not allowed";
                return false;
            }
        }
        else
        {
            var host = parsedUri.Host.TrimEnd('.').ToLowerInvariant();
            if (host == "localhost" ||
                host == "localhost.localdomain" ||
                host.EndsWith(".localhost", StringComparison.Ordinal) ||
                host.EndsWith(".local", StringComparison.Ordinal))
            {
                reason = "Local hostnames are not allowed";
                return false;
            }
        }

        safeUri = parsedUri;
        reason = string.Empty;
        return true;
    }

    private static bool IsPublicRoutableIp(IPAddress ipAddress)
    {
        if (IPAddress.IsLoopback(ipAddress) ||
            ipAddress.Equals(IPAddress.Any) ||
            ipAddress.Equals(IPAddress.None) ||
            ipAddress.Equals(IPAddress.IPv6Any) ||
            ipAddress.Equals(IPAddress.IPv6None) ||
            ipAddress.Equals(IPAddress.IPv6Loopback))
        {
            return false;
        }

        if (ipAddress.IsIPv4MappedToIPv6)
        {
            return IsPublicRoutableIp(ipAddress.MapToIPv4());
        }

        if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ipAddress.IsIPv6Multicast || ipAddress.IsIPv6LinkLocal || ipAddress.IsIPv6SiteLocal)
            {
                return false;
            }

            // Unique local addresses fc00::/7.
            var bytes = ipAddress.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            return true;
        }

        var ipv4Bytes = ipAddress.GetAddressBytes();
        if (ipv4Bytes.Length != 4)
        {
            return false;
        }

        var first = ipv4Bytes[0];
        var second = ipv4Bytes[1];

        if (first == 0 || first == 10 || first == 127)
        {
            return false;
        }

        if (first == 169 && second == 254)
        {
            return false;
        }

        if (first == 172 && second >= 16 && second <= 31)
        {
            return false;
        }

        if (first == 192 && second == 168)
        {
            return false;
        }

        // Carrier-grade NAT block 100.64.0.0/10.
        if (first == 100 && second >= 64 && second <= 127)
        {
            return false;
        }

        // Benchmarking block 198.18.0.0/15.
        if (first == 198 && (second == 18 || second == 19))
        {
            return false;
        }

        // Multicast/reserved.
        if (first >= 224)
        {
            return false;
        }

        return true;
    }
}
