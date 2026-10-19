using System.Net;
using System.Text;
using allstarr.Controllers;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests.Api;

public sealed class ProviderTryMediaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "provider-try-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _user = Guid.NewGuid();
    private readonly TestClock _clock = new();

    [Fact]
    public async Task Tickets_are_unpredictable_owned_and_expire_with_private_files()
    {
        using var store = CreateStore();
        var ticket = await AddFile(store);
        Assert.True(Convert.FromHexString(ticket.Token).Length >= 16);
        Assert.Null(store.Find(Guid.NewGuid(), ticket.Token));
        Assert.NotNull(store.Find(_user, ticket.Token));
        Assert.Single(Directory.GetFiles(_directory));
        _clock.Now += TimeSpan.FromMinutes(15);
        Assert.Null(store.Find(_user, ticket.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Fifth_ticket_evicts_oldest_for_the_same_user()
    {
        using var store = CreateStore();
        var oldest = await AddFile(store);
        for (var index = 0; index < 4; index++) await AddFile(store);
        Assert.Null(store.Find(_user, oldest.Token));
        Assert.Equal(4, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task Temporary_byte_limit_rejects_and_deletes_partial_copy()
    {
        using var store = CreateStore(3);
        await Assert.ThrowsAsync<IOException>(() => AddFile(store));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Startup_removes_only_private_temporary_files()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "old.media"), "old");
        File.WriteAllText(Path.Combine(_directory, "old.part"), "old");
        File.WriteAllText(Path.Combine(_directory, "keep.txt"), "keep");
        using var store = CreateStore();
        Assert.Equal("keep.txt", Path.GetFileName(Assert.Single(Directory.GetFiles(_directory))));
    }

    [Fact]
    public async Task Administrator_and_bound_identity_are_required()
    {
        using var store = CreateStore();
        var ticket = await AddFile(store);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Controller(store, administrator: false).Get(ticket.Token)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Controller(store, user: Guid.Empty).Get(ticket.Token)).StatusCode);
        Assert.IsType<NotFoundResult>(await Controller(store, Guid.NewGuid()).Get(ticket.Token));
    }

    [Theory]
    [InlineData("bytes=2-4", 206, "234", "bytes 2-4/10")]
    [InlineData("bytes=-2", 206, "89", "bytes 8-9/10")]
    [InlineData("bytes=30-", 416, "", "bytes */10")]
    [InlineData("bytes=0-1,3-4", 416, "", "bytes */10")]
    public async Task Local_media_preserves_single_range_semantics(string range, int status, string body, string contentRange)
    {
        using var store = CreateStore();
        var ticket = await AddFile(store);
        var controller = Controller(store);
        controller.Request.Headers.Range = range;
        await Execute(controller, await controller.Get(ticket.Token));
        Assert.Equal(status, controller.Response.StatusCode);
        Assert.Equal(body, Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray()));
        Assert.Equal(contentRange, controller.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(body.Length, controller.Response.ContentLength);
    }

    [Fact]
    public async Task Download_disposition_and_media_headers_are_preserved()
    {
        using var store = CreateStore();
        var ticket = await AddFile(store);
        var controller = Controller(store);
        await Execute(controller, await controller.Get(ticket.Token, "1"));
        Assert.Equal("audio/flac", controller.Response.ContentType);
        Assert.Equal(10, controller.Response.ContentLength);
        Assert.StartsWith("attachment;", controller.Response.Headers.ContentDisposition.ToString());
        Assert.DoesNotContain("source", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task Foreign_and_expired_tickets_do_not_open_the_protected_source()
    {
        using var store = CreateStore();
        var opens = 0;
        var ticket = store.CreateStream(_user, (_, _) =>
        {
            opens++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, "audio/flac", "sample.flac");
        Assert.IsType<NotFoundResult>(await Controller(store, Guid.NewGuid()).Get(ticket.Token));
        _clock.Now += TimeSpan.FromMinutes(15);
        Assert.IsType<NotFoundResult>(await Controller(store).Get(ticket.Token));
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task Invalid_upstream_partial_response_is_rejected_without_media_headers()
    {
        using var store = CreateStore();
        var content = new TrackingContent("23");
        var ticket = store.CreateStream(_user, (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            content.Headers.ContentRange = new(4, 5, 10);
            return Task.FromResult(response);
        }, "audio/flac", "sample.flac", 10);
        var controller = Controller(store);
        controller.Request.Headers.Range = "bytes=2-3";
        Assert.Equal(502, Assert.IsType<ObjectResult>(await controller.Get(ticket.Token)).StatusCode);
        Assert.True(content.Disposed);
        Assert.Empty(controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task Inline_is_the_default_and_cancellation_cleans_partial_files()
    {
        using var store = CreateStore();
        var ticket = await AddFile(store);
        var controller = Controller(store);
        await Execute(controller, await controller.Get(ticket.Token));
        Assert.StartsWith("inline;", controller.Response.Headers.ContentDisposition.ToString());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = Path.Combine(_directory, "source.txt");
        await File.WriteAllTextAsync(source, "sample");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateFileAsync(
            _user, source, "audio/flac", "sample.flac", cancellation.Token));
        Assert.Empty(Directory.GetFiles(_directory, "*.part"));
    }

    [Fact]
    public async Task Streaming_factory_receives_range_and_response_is_disposed()
    {
        using var store = CreateStore();
        string? receivedRange = null;
        var content = new TrackingContent("23");
        var ticket = store.CreateStream(_user, (range, _) =>
        {
            receivedRange = range;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            response.Content.Headers.ContentRange = new(2, 3, 10);
            return Task.FromResult(response);
        }, "audio/flac", "sample.flac", 10);
        var controller = Controller(store);
        controller.Request.Headers.Range = "bytes=2-3";
        await Execute(controller, await controller.Get(ticket.Token));
        Assert.Equal("bytes=2-3", receivedRange);
        Assert.Equal(206, controller.Response.StatusCode);
        Assert.Equal("23", Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray()));
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Streaming_full_response_can_supply_a_known_length_range()
    {
        using var store = CreateStore();
        var ticket = store.CreateStream(_user, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("0123456789"))
        }), "audio/flac", "sample.flac", 10);
        var controller = Controller(store);
        controller.Request.Headers.Range = "bytes=7-";
        await Execute(controller, await controller.Get(ticket.Token));
        Assert.Equal(206, controller.Response.StatusCode);
        Assert.Equal("789", Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray()));
    }

    [Theory]
    [InlineData("bytes=0-", 200, "0123456789")]
    [InlineData("bytes=4-", 416, "")]
    public async Task Streaming_without_a_known_length_serves_the_whole_song_from_the_start(string range, int status, string body)
    {
        using var store = CreateStore();
        var ticket = store.CreateStream(_user, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent("0123456789")
        }), "audio/flac", "sample.flac", null);
        var controller = Controller(store);
        controller.Request.Headers.Range = range;
        await Execute(controller, await controller.Get(ticket.Token));
        Assert.Equal(status, controller.Response.StatusCode);
        Assert.Equal(body, Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray()));
        Assert.False(controller.Response.Headers.ContainsKey("Content-Range") && status == 200);
    }

    private sealed class UnknownLengthContent(string text) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private ProviderTryMediaStore CreateStore(long maximumBytes = ProviderTryMediaStore.DefaultMaximumTemporaryBytes) =>
        new(_clock, _directory, maximumBytes);

    private async Task<ProviderTryMediaTicket> AddFile(ProviderTryMediaStore store)
    {
        var source = Path.Combine(Path.GetTempPath(), "provider-try-source-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(source, "0123456789");
        try { return await store.CreateFileAsync(_user, source, "audio/flac", "sample.flac"); }
        finally { File.Delete(source); }
    }

    private ProviderTryMediaController Controller(ProviderTryMediaStore store, Guid? user = default, bool administrator = true)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        http.RequestServices = services.BuildServiceProvider();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "test",
            UserId = "test",
            UserName = "test",
            IsAdministrator = administrator,
            AllstarrUserId = user == Guid.Empty ? null : user ?? _user,
            JellyfinAccessToken = "",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new ProviderTryMediaController(store, NullLogger<ProviderTryMediaController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static Task Execute(ControllerBase controller, IActionResult result) => result.ExecuteResultAsync(controller.ControllerContext);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TrackingContent(string body) : ByteArrayContent(Encoding.UTF8.GetBytes(body))
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
