using System.Net;
using System.Net.Sockets;

namespace allstarr.Services.Common;

/// <summary>
/// Guards outbound HTTP(S) requests that are derived from external metadata.
/// Blocks local/private targets to reduce SSRF risk.
/// </summary>
public static class OutboundRequestGuard
{
    private static readonly byte[] Ipv6Documentation =
        IPAddress.Parse("2001:db8::").GetAddressBytes();
    private static readonly byte[] Ipv6DiscardOnly =
        IPAddress.Parse("100::").GetAddressBytes();
    private static readonly byte[] Ipv6Nat64 =
        IPAddress.Parse("64:ff9b::").GetAddressBytes();
    private static readonly byte[] Ipv6Nat64LocalUse =
        IPAddress.Parse("64:ff9b:1::").GetAddressBytes();
    private static readonly byte[] Ipv6CompatibleIpv4 =
        IPAddress.IPv6Any.GetAddressBytes();
    private static readonly byte[] Ipv6SixToFour =
        IPAddress.Parse("2002::").GetAddressBytes();
    private static readonly byte[] Ipv6Teredo =
        IPAddress.Parse("2001::").GetAddressBytes();

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

    /// <summary>
    /// Validates an administrator-configured service endpoint. Unlike metadata-derived
    /// URLs, this may intentionally target an RFC1918 address or Docker/LAN hostname.
    /// Redirects still must be disabled by the caller, and credentials must never be
    /// embedded in the URL.
    /// </summary>
    public static bool TryCreateConfiguredServiceUri(
        string? rawUrl,
        out Uri? serviceUri,
        out string reason)
    {
        serviceUri = null;
        reason = "URL is empty";
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsedUri))
        {
            reason = string.IsNullOrWhiteSpace(rawUrl) ? "URL is empty" : "URL must be absolute";
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

        if (!string.IsNullOrEmpty(parsedUri.Query) || !string.IsNullOrEmpty(parsedUri.Fragment))
        {
            reason = "Query strings and fragments are not allowed on a service base URL";
            return false;
        }

        if (parsedUri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 &&
            IPAddress.TryParse(parsedUri.Host, out var address) &&
            (address.Equals(IPAddress.Any) ||
             address.Equals(IPAddress.None) ||
             address.Equals(IPAddress.IPv6Any) ||
             address.Equals(IPAddress.IPv6None) ||
             address.IsIPv6Multicast))
        {
            reason = "Unspecified and multicast service hosts are not allowed";
            return false;
        }

        serviceUri = new Uri(parsedUri.AbsoluteUri.TrimEnd('/') + "/");
        reason = string.Empty;
        return true;
    }

    public static bool IsPublicRoutableIp(IPAddress ipAddress)
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

            // Documentation, discard-only, and IPv4 translation ranges are not
            // valid public destinations for a server-side proxy.
            if (IsInPrefix(bytes, Ipv6Documentation, 32) ||
                IsInPrefix(bytes, Ipv6DiscardOnly, 64) ||
                IsInPrefix(bytes, Ipv6Nat64, 96) ||
                IsInPrefix(bytes, Ipv6Nat64LocalUse, 48) ||
                IsInPrefix(bytes, Ipv6CompatibleIpv4, 96) ||
                IsInPrefix(bytes, Ipv6SixToFour, 16) ||
                IsInPrefix(bytes, Ipv6Teredo, 32))
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

        if (first == 192 && second == 0 &&
            (ipv4Bytes[2] == 0 || ipv4Bytes[2] == 2))
        {
            return false;
        }

        if (first == 192 && second == 88 && ipv4Bytes[2] == 99)
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

        if ((first == 198 && second == 51 && ipv4Bytes[2] == 100) ||
            (first == 203 && second == 0 && ipv4Bytes[2] == 113))
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

    private static bool IsInPrefix(byte[] address, byte[] prefix, int prefixLength)
    {
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (address.Length < wholeBytes || prefix.Length < wholeBytes)
        {
            return false;
        }

        for (var index = 0; index < wholeBytes; index++)
        {
            if (address[index] != prefix[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
    }
}
