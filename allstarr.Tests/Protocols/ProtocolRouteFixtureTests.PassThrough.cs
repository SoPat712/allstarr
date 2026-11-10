using System.Net;
using System.Text;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinImage_LocalRelayPreservesBytesCacheHeadersAndConditionalNotModified()
    {
        var verifications = 0;
        var itemLookups = 0;
        var images = 0;
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me")
            {
                verifications++;
                return Json(200, """{"Id":"listener"}""");
            }
            if (path == "/Items")
            {
                itemLookups++;
                return ItemLookup("""{"Id":"local-track","Type":"Audio"}""");
            }
            Assert.StartsWith("/Items/local-track/Images/Primary", path);
            Assert.Contains("fillWidth=600&quality=90&tag=art", request.RequestUri.Query);
            images++;
            var conditional = request.Headers.IfNoneMatch.Any(value => value.Tag == "\"art\"");
            if (conditional) Assert.NotNull(request.Headers.IfModifiedSince);
            var response = new HttpResponseMessage(conditional ? HttpStatusCode.NotModified : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(conditional ? [] : new byte[] { 0, 1, 2, 255 })
            };
            response.Headers.ETag = new("\"art\"");
            response.Headers.CacheControl = new() { Public = true, MaxAge = TimeSpan.FromHours(1) };
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            response.Content.Headers.ContentType = new("image/jpeg");
            return response;
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "image-test-token");
        for (var index = 0; index < 3; index++)
        {
            var path = "/Items/local-track/Images/Primary" + (index == 1 ? "/0" : "") +
                "?fillWidth=600&quality=90&tag=art";
            using var request = new HttpRequestMessage(index == 2 ? HttpMethod.Head : HttpMethod.Get, path);
            if (index == 1)
            {
                request.Headers.IfNoneMatch.Add(new("\"art\""));
                request.Headers.IfModifiedSince = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            }
            using var response = await client.SendAsync(request);
            Assert.Equal(index == 1 ? HttpStatusCode.NotModified : HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"art\"", response.Headers.ETag?.Tag);
            Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl?.MaxAge);
            Assert.NotNull(response.Content.Headers.LastModified);
            Assert.Equal(index == 0 ? new byte[] { 0, 1, 2, 255 } : [], await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(1, verifications);
        Assert.Equal(1, itemLookups);
        Assert.Equal(3, images);
    }

    [Fact]
    public async Task JellyfinProxy_LocalAudioDeliversFirstBytesBeforeRemainderAndEvictsUnauthorized()
    {
        var verifications = 0;
        var itemLookups = 0;
        var streams = 0;
        var revoked = false;
        var body = new GatedAudioBody();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me")
            {
                verifications++;
                return Json(200, """{"Id":"listener"}""");
            }
            if (path == "/Items")
            {
                itemLookups++;
                return ItemLookup("""{"Id":"local-track","Type":"Audio"}""");
            }
            if (path.Contains("/Images/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
            Assert.Equal("/Audio/local-track/stream", path);
            streams++;
            if (revoked) return Json(401, "{}");
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("audio/flac");
            response.Content.Headers.ContentRange = new(0, body.Length - 1, body.Length);
            response.Content.Headers.ContentLength = body.Length;
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "audio-test-token");
        // Warm the same verification and music-item caches used by the audio route.
        using (await client.GetAsync("/Items/local-track/Images/Primary")) { }
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Audio/local-track/stream?static=true");
        request.Headers.Range = new(0, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var first = new byte[4];
        await stream.ReadExactlyAsync(first.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Encoding.ASCII.GetBytes("fLaC"), first);
        Assert.False(body.Release.Task.IsCompleted);
        Assert.Equal(1, verifications);
        Assert.Equal(1, itemLookups);
        body.Release.SetResult();
        await stream.CopyToAsync(Stream.Null);
        revoked = true;
        using (var failure = await client.GetAsync("/Audio/local-track/stream?static=true"))
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        using (await client.GetAsync("/Items/local-track/Images/Primary")) { }
        Assert.Equal(2, verifications);
        Assert.Equal(2, streams);
    }

    [Fact]
    public async Task JellyfinProxy_ArtistBrowsePreservesExactNativeBytesAndOrdering()
    {
        const string upstream = """{ "Items": [{"Id":"b","Type":"MusicArtist","Name":"B & <B>"},{"Id":"a","Type":"MusicArtist","Name":"A"}], "TotalRecordCount":2, "StartIndex":0 }""";
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, upstream));
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        using var response = await client.GetAsync("/Artists/AlbumArtists?limit=200&api_key=browse-test-token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(upstream, await response.Content.ReadAsStringAsync());
    }

    private sealed class GatedAudioBody : Stream
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long Length => 23 * 1024 * 1024;
        public override long Position { get; set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0)
            {
                Encoding.ASCII.GetBytes("fLaC").CopyTo(buffer);
                Position = 4;
                return 4;
            }
            await Release.Task.WaitAsync(cancellationToken);
            var count = (int)Math.Min(buffer.Length, Length - Position);
            buffer.Span[..count].Clear();
            Position += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
