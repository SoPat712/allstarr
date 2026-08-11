using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using allstarr.Middleware;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public class WebSocketProxyMiddlewareTests
{
    [Fact]
    public void BuildMaskedQuery_RedactsSensitiveParams()
    {
        var qs = "?api_key=secret&deviceId=abc&token=othertoken";
        var masked = allstarr.Middleware.WebSocketProxyMiddleware.BuildMaskedQuery(qs);

        Assert.Contains("api_key=<redacted>", masked);
        Assert.Contains("deviceId=abc", masked);
        Assert.Contains("token=<redacted>", masked);
        Assert.DoesNotContain("secret", masked);
        Assert.DoesNotContain("othertoken", masked);
    }

    [Fact]
    public void BuildMaskedQuery_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, allstarr.Middleware.WebSocketProxyMiddleware.BuildMaskedQuery(null));
        Assert.Equal(string.Empty, allstarr.Middleware.WebSocketProxyMiddleware.BuildMaskedQuery(string.Empty));
    }

    [Fact]
    public async Task ProxyMessagesAsync_ReassemblesTextAndBinaryFramesAndForwardsClose()
    {
        var source = new ScriptedWebSocket(
            Frame.Text("hello ", endOfMessage: false),
            Frame.Text("world"),
            Frame.Binary(Enumerable.Range(0, 3_000).Select(index => (byte)(index % 251)).ToArray(), false),
            Frame.Binary(Enumerable.Range(3_000, 2_000).Select(index => (byte)(index % 251)).ToArray(), false),
            Frame.Binary([1, 2, 3]),
            Frame.Close(WebSocketCloseStatus.NormalClosure, "finished"));
        var destination = new ScriptedWebSocket();
        var middleware = CreateMiddleware();

        await middleware.ProxyMessagesAsync(source, destination, "test", CancellationToken.None);

        Assert.Collection(
            destination.Sent,
            message =>
            {
                Assert.Equal(WebSocketMessageType.Text, message.Type);
                Assert.Equal("hello world", Encoding.UTF8.GetString(message.Data));
            },
            message =>
            {
                Assert.Equal(WebSocketMessageType.Binary, message.Type);
                Assert.Equal(5_003, message.Data.Length);
                Assert.Equal([1, 2, 3], message.Data[^3..]);
            });
        Assert.Equal(WebSocketCloseStatus.NormalClosure, destination.CloseStatus);
        Assert.Equal("finished", destination.CloseStatusDescription);
    }

    private static WebSocketProxyMiddleware CreateMiddleware() => new(
        _ => Task.CompletedTask,
        Options.Create(new JellyfinSettings { Url = "http://localhost:8096" }),
        LoggerFactory.Create(_ => { }).CreateLogger<WebSocketProxyMiddleware>(),
        []);

    private sealed record Frame(
        WebSocketMessageType Type,
        byte[] Data,
        bool EndOfMessage = true,
        WebSocketCloseStatus? CloseStatus = null,
        string? CloseDescription = null)
    {
        public static Frame Text(string value, bool endOfMessage = true) =>
            new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(value), endOfMessage);
        public static Frame Binary(byte[] value, bool endOfMessage = true) =>
            new(WebSocketMessageType.Binary, value, endOfMessage);
        public static Frame Close(WebSocketCloseStatus status, string description) =>
            new(WebSocketMessageType.Close, [], true, status, description);
    }

    private sealed class ScriptedWebSocket(params Frame[] frames) : WebSocket
    {
        private readonly Queue<Frame> _frames = new(frames);
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private WebSocketState _state = WebSocketState.Open;

        public List<(WebSocketMessageType Type, byte[] Data)> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = _frames.Dequeue();
            frame.Data.AsSpan().CopyTo(buffer.AsSpan());
            if (frame.Type == WebSocketMessageType.Close)
            {
                _closeStatus = frame.CloseStatus;
                _closeStatusDescription = frame.CloseDescription;
                _state = WebSocketState.CloseReceived;
            }

            return Task.FromResult(new WebSocketReceiveResult(
                frame.Data.Length,
                frame.Type,
                frame.EndOfMessage,
                frame.CloseStatus,
                frame.CloseDescription));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Sent.Add((messageType, buffer.ToArray()));
            return Task.CompletedTask;
        }
    }
}
