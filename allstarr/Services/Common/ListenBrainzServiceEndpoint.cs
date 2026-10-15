using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace allstarr.Services.Common;

public static class ListenBrainzServiceEndpoint
{
    private static readonly Uri DefaultBaseUri = new("https://api.listenbrainz.org/1/");
    public static bool IsCompatibleTarget(string providerId) => providerId is "listenbrainz" or "multi-scrobbler" or "koito";

    public static Uri FromSecret(JsonElement secret, string providerId = "listenbrainz") =>
        Parse(secret.EnumerateObject().FirstOrDefault(x => x.Name.Equals("baseUrl", StringComparison.OrdinalIgnoreCase)).Value is
        { ValueKind: JsonValueKind.String } value ? value.GetString() : null, providerId);

    public static Uri FromSecret(IReadOnlyDictionary<string, string>? secret, string providerId = "listenbrainz") =>
        Parse(secret?.FirstOrDefault(item =>
            string.Equals(item.Key, "baseUrl", StringComparison.OrdinalIgnoreCase)).Value, providerId);

    public static Uri Route(Uri baseUri, string relativePath) => new(baseUri, relativePath.TrimStart('/'));

    private static Uri Parse(string? value, string providerId)
    {
        if (string.IsNullOrWhiteSpace(value))
            return providerId == "listenbrainz" ? DefaultBaseUri : throw new InvalidOperationException("A listening service base URL is required.");
        if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(value, out var result, out _) ||
            result!.Scheme == Uri.UriSchemeHttp && !IsPrivateHttpHost(result.Host))
            throw new InvalidOperationException("Use an exact HTTPS base URL, or HTTP with a private address or intranet host name.");
        return result;
    }

    private static bool IsPrivateHttpHost(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var address)) return IsPrivateAddress(address);
        return !host.Contains('.') || host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".lan", StringComparison.Ordinal) ||
            host.EndsWith(".internal", StringComparison.Ordinal) || host.EndsWith(".home.arpa", StringComparison.Ordinal);
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return (bytes[0] & 0xfe) == 0xfc;
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }

    internal static bool AllowsResolvedAddresses(Uri uri, IReadOnlyList<IPAddress> addresses) =>
        addresses.Count > 0 && (uri.Scheme == Uri.UriSchemeHttps || addresses.All(IsPrivateAddress));

    public static SocketsHttpHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
            if (!AllowsResolvedAddresses(context.InitialRequestMessage.RequestUri!, addresses))
                throw new HttpRequestException("A private HTTP listening service resolved outside private networks.");
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception) when (!token.IsCancellationRequested) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("The listening service could not be reached.");
        }
    };
}
