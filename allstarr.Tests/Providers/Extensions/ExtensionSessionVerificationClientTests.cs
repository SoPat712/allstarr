using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using allstarr.Core.Extensions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed class ExtensionSessionVerificationClientTests
{
    [Fact]
    public async Task VerificationSendsJsonWithContentLengthToTheSidecar()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string callback = "spotiflac://session-grant?state=fixture&grant=fixture-grant";
        var challenge = new ExtensionSessionChallenge(
            "https://api.zarz.moe/v2/challenge?id=fixture&cb=spotiflac%3A%2F%2Fsession-grant%3Fstate%3Dfixture",
            "2026-10-20T00:00:00Z");
        var serving = ServeAsync();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(ExtensionSessionVerificationClient.HttpClientName))
            .Returns(() => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Extensions:SessionVerification:BaseUrl"] = $"http://127.0.0.1:{port}" }).Build();
        var client = new ExtensionSessionVerificationClient(clients.Object, configuration);
        string? result = null;

        var failure = await Record.ExceptionAsync(async () => result = await client.VerifyAsync(challenge, deadline.Token));
        var observed = await serving;

        Assert.Equal("POST /api/verify HTTP/1.1", observed.RequestLine);
        Assert.InRange(observed.ContentLength, 1, 16 * 1024);
        Assert.Null(observed.TransferEncoding);
        Assert.StartsWith("application/json", observed.ContentType, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(observed.Body);
        Assert.Equal(challenge.VerificationUrl, body.RootElement.GetProperty("verification_url").GetString());
        Assert.Single(body.RootElement.EnumerateObject());
        Assert.Null(failure);
        Assert.Equal(callback, result);

        async Task<(string? RequestLine, int ContentLength, string? TransferEncoding, string? ContentType, string Body)> ServeAsync()
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(deadline.Token);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
            {
                var separator = line.IndexOf(':');
                headers.Add(line[..separator], line[(separator + 1)..].Trim());
            }
            var length = int.TryParse(headers.GetValueOrDefault("Content-Length"), out var value) ? value : 0;
            var accepted = length is > 0 and <= 16 * 1024;
            var contents = new char[accepted ? length : 0];
            if (accepted) await reader.ReadBlockAsync(contents, deadline.Token);
            var response = JsonSerializer.Serialize(accepted ? new { callback_url = callback } : (object)new { error = "unsupported_challenge" });
            var bytes = Encoding.UTF8.GetBytes(response);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.0 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, deadline.Token);
            await stream.WriteAsync(bytes, deadline.Token);
            return (requestLine, length, headers.GetValueOrDefault("Transfer-Encoding"), headers.GetValueOrDefault("Content-Type"), new(contents));
        }
    }
}
