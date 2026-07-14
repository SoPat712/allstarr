using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text.Json;

namespace allstarr.Services.Common;

public interface IPublicEndpointDnsResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken);
}

public sealed class SystemPublicEndpointDnsResolver : IPublicEndpointDnsResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public interface IResolvedIpConnector
{
    ValueTask<Stream> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken);
}

public sealed class SocketResolvedIpConnector : IResolvedIpConnector
{
    public async ValueTask<Stream> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class PublicEndpointConnector(
    IPublicEndpointDnsResolver dnsResolver,
    IResolvedIpConnector ipConnector)
{
    public async ValueTask<Stream> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            throw new HttpRequestException("The proxy endpoint is invalid.");
        }

        IReadOnlyList<IPAddress> resolved;
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            resolved = [literalAddress];
        }
        else
        {
            var normalizedHost = host.TrimEnd('.');
            if (normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                normalizedHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("The proxy endpoint is not publicly routable.");
            }

            resolved = await dnsResolver.ResolveAsync(normalizedHost, cancellationToken);
        }

        var addresses = resolved.Distinct().ToArray();
        if (addresses.Length == 0 || addresses.Any(address =>
                !OutboundRequestGuard.IsPublicRoutableIp(address)))
        {
            throw new HttpRequestException("The proxy endpoint is not publicly routable.");
        }

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            try
            {
                // Connect to the exact validated address. No second DNS lookup can
                // replace it with a private address between validation and connect.
                return await ipConnector.ConnectAsync(address, port, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
        }

        throw new HttpRequestException("The proxy endpoint connection failed.", lastFailure);
    }
}

public interface ISafeProxyTransportFactory
{
    HttpMessageHandler CreateHandler();
}

public sealed class SafeProxyTransportFactory(
    PublicEndpointConnector endpointConnector) : ISafeProxyTransportFactory
{
    public HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip |
                                 DecompressionMethods.Deflate |
                                 DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = (context, cancellationToken) => endpointConnector.ConnectAsync(
            context.DnsEndPoint.Host,
            context.DnsEndPoint.Port,
            cancellationToken)
    };
}

public enum SafeJsonProxyOutcome
{
    Success,
    Unavailable,
    ResponseTooLarge,
    InvalidPayload
}

public sealed record SafeJsonProxyResult(
    SafeJsonProxyOutcome Outcome,
    JsonElement? Payload = null);

public interface ISafeJsonProxyClient
{
    Task<SafeJsonProxyResult> GetAsync(
        Uri endpoint,
        long maximumResponseBytes,
        CancellationToken cancellationToken = default);
}

public sealed class SafeJsonProxyClient(
    ISafeProxyTransportFactory transportFactory) : ISafeJsonProxyClient
{
    public async Task<SafeJsonProxyResult> GetAsync(
        Uri endpoint,
        long maximumResponseBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumResponseBytes is < 1 or > 16 * 1024 * 1024 ||
            !OutboundRequestGuard.TryCreateSafeHttpUri(
                endpoint.AbsoluteUri,
                out var safeEndpoint,
                out _))
        {
            return new SafeJsonProxyResult(SafeJsonProxyOutcome.Unavailable);
        }

        using var handler = transportFactory.CreateHandler();
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, safeEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Redirects are deliberately returned here as failures. The dedicated
            // production handler never follows a Location to a second host.
            return new SafeJsonProxyResult(SafeJsonProxyOutcome.Unavailable);
        }

        if (response.Content.Headers.ContentLength > maximumResponseBytes)
        {
            return new SafeJsonProxyResult(SafeJsonProxyOutcome.ResponseTooLarge);
        }

        try
        {
            await response.Content.LoadIntoBufferAsync(maximumResponseBytes, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 64 },
                cancellationToken);
            return new SafeJsonProxyResult(
                SafeJsonProxyOutcome.Success,
                document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new SafeJsonProxyResult(SafeJsonProxyOutcome.InvalidPayload);
        }
    }
}
