using System.Net;
using System.Net.Sockets;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.ArtistGrid;

internal static class ArtistGridHttpTransport
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, token) => ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token)
    };

    internal static async ValueTask<Stream> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        var addresses = await (resolve ?? Dns.GetHostAddressesAsync)(host, cancellationToken).ConfigureAwait(false);
        if (port != 443 || addresses.Length == 0 || addresses.Any(address => !OutboundRequestGuard.IsPublicRoutableIp(address)))
            throw new HttpRequestException("The provider host did not resolve to public addresses.");

        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await (connect ?? ConnectSocketAsync)(address, port, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException) { }
        }
        throw new HttpRequestException("The provider host could not be reached.");
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
