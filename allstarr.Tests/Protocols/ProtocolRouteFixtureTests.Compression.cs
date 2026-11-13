using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("gzip", "ranked")]
    [InlineData("br", "ranked")]
    [InlineData(null, "ranked")]
    [InlineData("gzip", null)]
    public async Task ClientApi_SearchCompressionPreservesFlushedBatches(string? encoding, string? shape)
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "cover", 40, true, true, true, true,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, string _, int _, bool _, bool _, bool _, bool _,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("deezer", new SearchResult
                {
                    Songs = Enumerable.Range(0, 40).Select(index => new Song
                    {
                        Id = $"ext-deezer-song-cover-{index}",
                        ExternalProvider = "deezer",
                        ExternalId = $"cover-{index}",
                        Title = $"Cover {index}",
                        Artist = "Artist",
                        Album = "Release",
                        Duration = 200
                    }).ToList()
                }, [], token);
                await finish.Task.WaitAsync(token);
                return (IReadOnlyList<string>)[];
            });
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/System/Info/Public" => Json(200, """{"Id":"native-server"}"""),
            _ => Json(200, """{"Items":[]}""")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/allstarr/v1/search?q=cover&limit=40" +
            (shape == null ? "" : "&shape=" + shape));
        if (encoding != null) request.Headers.AcceptEncoding.ParseAdd(encoding);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var actual = response.Content.Headers.ContentEncoding.SingleOrDefault();
            await using var raw = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using Stream decoded = actual switch
            {
                "gzip" => new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true),
                "br" => new BrotliStream(raw, CompressionMode.Decompress, leaveOpen: true),
                _ => raw
            };
            using var reader = new StreamReader(decoded);
            var first = JsonNode.Parse((await reader.ReadLineAsync(timeout.Token))!)!;
            Assert.False(finish.Task.IsCompleted);
            Assert.Equal("ext-deezer-song-cover-0", shape == "ranked"
                ? first["sections"]!["songs"]![0]!["Id"]!.GetValue<string>()
                : first["items"]![0]!["Id"]!.GetValue<string>());
            if (shape == "ranked") Assert.False(first["done"]!.GetValue<bool>());
            _lyricsSearchOutput.WriteLine($"Search {shape ?? "batches"}: encoding={actual ?? "identity"}; first batch arrived while provider completion was held; {string.Join(", ", response.Headers.GetValues("Server-Timing"))}");
            finish.TrySetResult();
            var remaining = await reader.ReadToEndAsync(timeout.Token);
            var last = JsonNode.Parse(remaining.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1])!;
            Assert.True(last["done"]!.GetValue<bool>());
            if (shape == "ranked") Assert.Equal(40, last["sections"]!["songs"]!.AsArray().Count);
            Assert.Equal(encoding, actual);
            using var completedRequest = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
            if (encoding != null) completedRequest.Headers.AcceptEncoding.ParseAdd(encoding);
            using var completed = await client.SendAsync(completedRequest, timeout.Token);
            var wire = await completed.Content.ReadAsByteArrayAsync(timeout.Token);
            using var wireStream = new MemoryStream(wire);
            using Stream content = encoding switch
            {
                "gzip" => new GZipStream(wireStream, CompressionMode.Decompress),
                "br" => new BrotliStream(wireStream, CompressionMode.Decompress),
                _ => wireStream
            };
            using var contentReader = new StreamReader(content);
            var json = await contentReader.ReadToEndAsync(timeout.Token);
            _lyricsSearchOutput.WriteLine($"Search {shape ?? "batches"}: wire={wire.Length} bytes; JSON={System.Text.Encoding.UTF8.GetByteCount(json)} bytes.");
            if (encoding != null) client.DefaultRequestHeaders.AcceptEncoding.ParseAdd(encoding);
            foreach (var concurrent in await ReadConcurrentTimingAsync(client, request.RequestUri!.PathAndQuery))
            {
                using var batch = new MemoryStream(concurrent);
                using Stream decodedBatch = encoding switch
                {
                    "gzip" => new GZipStream(batch, CompressionMode.Decompress),
                    "br" => new BrotliStream(batch, CompressionMode.Decompress),
                    _ => batch
                };
                using var batchReader = new StreamReader(decodedBatch);
                var frames = (await batchReader.ReadToEndAsync(timeout.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var done = JsonNode.Parse(frames[^1])!;
                Assert.True(done["done"]!.GetValue<bool>());
                Assert.True(shape == "ranked" ? JsonNode.DeepEquals(last["sections"], done["sections"]) :
                    JsonNode.DeepEquals(first["items"], JsonNode.Parse(frames[0])!["items"]));
            }
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task ClientApi_RankedSearchAbortStopsSourcesAndReturnsTheHandler()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "cancel", 40, true, true, true, true,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, string _, int _, bool _, bool _, bool _, bool _,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                try
                {
                    await write("deezer", new SearchResult
                    {
                        Songs = [new Song
                        { Id = "ext-deezer-song-cancel", ExternalProvider = "deezer", ExternalId = "cancel", Title = "Cancel", Artist = "Artist", Duration = 200 }]
                    }, [], token);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return (IReadOnlyList<string>)[];
                }
                finally { stopped.TrySetResult(); }
            });
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[]}"""), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.AddSingleton<IStartupFilter>(new SearchCompletionFilter(completed));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await client.GetAsync("/allstarr/v1/search?q=cancel&limit=40&shape=ranked", HttpCompletionOption.ResponseHeadersRead, abort.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(abort.Token));
        Assert.NotNull(await reader.ReadLineAsync(abort.Token));
        abort.Cancel();
        response.Dispose();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class SearchCompletionFilter(TaskCompletionSource completed) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                try { await continuation(); }
                finally { if (context.Request.Path == "/allstarr/v1/search") completed.TrySetResult(); }
            });
            next(app);
        };
    }

    [Theory]
    [InlineData("/allstarr/v2/events")]
    public async Task ClientApi_EventCompressionRemainsDisabled(string path)
    {
        using var factory = V2EventsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        using var request = ClientPlaybackRequest(path);
        if (path.StartsWith("/allstarr/v2/", StringComparison.Ordinal)) request.Headers.Authorization = new("Bearer", "listener-token");
        request.Headers.AcceptEncoding.ParseAdd("br, gzip");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.NotNull(await reader.ReadLineAsync(timeout.Token));
    }
}
