using System.Net;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RetainedOriginal_CompleteBodySurvivesRestartWithoutUsingOrReplacingPlaybackBytes(bool id3Flac, bool fullRange)
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        byte[] converted = [9, 8, 7, 6];
        var convertedPath = await fixture.SeedPlaybackAsync(converted);
        var bytes = id3Flac ? ((byte[])[73, 68, 51, 4, 0, 0, 0, 0, 0, 0]).Concat("fLaC"u8.ToArray()).Concat(fixture.Bytes).ToArray() : fixture.Bytes;
        var extraOpens = 0;
        using (var response = fixture.Response(bytes))
        {
            if (fullRange)
            {
                response.StatusCode = HttpStatusCode.PartialContent;
                response.Content.Headers.ContentRange = new(0, bytes.Length - 1, bytes.Length);
            }
            var stream = fixture.Stream(response) with
            {
                OpenOriginalResponseAsync = _ => { extraOpens++; throw new InvalidOperationException("The complete foreground response must be reused."); }
            };
            var recorded = 0;
            stream.FileFacts.RecordAsync = (_, _) => { recorded++; return Task.CompletedTask; };
            await fixture.WrapAsync(stream);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(0, recorded);
        }
        var rows = await fixture.Mappings.ListCacheMappingsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, extraOpens);
        var original = Assert.Single(rows, item => item.ScopeKey == fixture.OriginalScope.Key);
        Assert.NotEqual(convertedPath, original.LocalPath);
        Assert.StartsWith(Path.Combine(fixture.Root, "cache") + Path.DirectorySeparatorChar, original.LocalPath, StringComparison.Ordinal);
        Assert.NotNull(original.CacheIdentityJson);
        using (var identity = JsonDocument.Parse(original.CacheIdentityJson))
        {
            Assert.Equal("Original", identity.RootElement.GetProperty("Representation").GetString());
            Assert.Equal(7, identity.RootElement.GetProperty("SourceAccountRevision").GetInt64());
        }
        await fixture.RestartCacheAsync();
        using var replay = (await fixture.OpenAsync())!.Response;
        Assert.Equal(bytes, await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal(converted, await File.ReadAllBytesAsync(convertedPath));
        Assert.True(File.Exists(original.LocalPath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.legacy-id3-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RetainedOriginal_DurableReplayRequiresTheSameAccountRevisionQualityAndPurpose()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        using (var response = fixture.Response())
        {
            await fixture.WrapAsync(fixture.Stream(response));
            await response.Content.CopyToAsync(Stream.Null);
        }
        Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
        await fixture.RestartCacheAsync();
        using var replay = (await fixture.OpenAsync())!.Response;
        Assert.Equal(fixture.Bytes, await replay.Content.ReadAsByteArrayAsync());
        Assert.Null(await fixture.OpenAsync(fixture.OriginalScope with { ProviderAccountId = Guid.CreateVersion7() }));
        Assert.Null(await fixture.OpenAsync(fixture.OriginalScope with { SourceAccountRevision = 8 }, revision: 8));
        Assert.Null(await fixture.OpenAsync(fixture.OriginalScope with { AudioQuality = ProviderAudioQuality.Lossless }));
        Assert.Null(await fixture.Cache.TryOpenAsync(fixture.Track, new(fixture.Account, ProviderAudioQuality.Any), default, 7));
        Assert.Null(await fixture.OpenAsync(retained: false));
    }

    [Fact]
    public async Task RetainedOriginal_AccountFreeFilesRetainAndReplayWithANullSourceRevision()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        using (var response = fixture.Response())
        {
            await fixture.WrapAsync(fixture.Stream(response) with { ServingAccountId = null, ServingAccountRevision = null });
            Assert.Equal(fixture.Bytes, await response.Content.ReadAsByteArrayAsync());
        }
        var mapping = Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
        Assert.True(DownloadedSongMappingScope.TryResolve(mapping, out var scope));
        Assert.Equal(DownloadedSongRepresentation.Original, scope.Representation);
        Assert.Null(scope.ProviderAccountId);
        Assert.Null(scope.SourceAccountRevision);
        await fixture.RestartCacheAsync();
        using var replay = (await fixture.Cache.TryOpenAsync(fixture.Track, scope, default,
            originalDownload: true, retainOriginalMedia: true))!.Response;
        Assert.Equal(fixture.Bytes, await replay.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RetainedOriginal_CanceledRequestMetadataCannotDiscardCompletedOriginalBytes()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        using var request = new CancellationTokenSource();
        request.Cancel();
        using var response = fixture.Response();
        await fixture.Cache.WrapAsync(fixture.Stream(response), fixture.Context, fixture.Track.ProviderId, fixture.Track.Value,
            false, () => Task.FromCanceled<allstarr.Models.Domain.Song?>(request.Token), default);
        Assert.Equal(fixture.Bytes, await response.Content.ReadAsByteArrayAsync());
        var mapping = Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(mapping.LocalPath));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("readonly")]
    [InlineData("unconsumed")]
    [InlineData("permanent")]
    [InlineData("temporary-original")]
    public async Task RetainedOriginal_NonRetainingUsesNeverPublishOrOpenAnAdditionalFullBody(string use)
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync(use == "permanent" ? StorageMode.Permanent : StorageMode.Cache);
        using var response = fixture.Response();
        var opens = 0;
        var stream = fixture.Stream(response) with
        {
            OpenOriginalResponseAsync = _ => { opens++; return Task.FromResult(fixture.Response()); }
        };
        var context = fixture.Context with { IsReadOnly = use == "readonly", RetainOriginalMedia = use != "temporary-original" };
        await fixture.WrapAsync(stream, context, headOnly: use == "head");
        if (use != "head" && use != "unconsumed") await response.Content.CopyToAsync(Stream.Null);
        response.Dispose();
        await fixture.RestartCacheAsync();
        Assert.Equal(0, opens);
        Assert.Empty(await fixture.Mappings.ListCacheMappingsAsync());
        Assert.Empty(Directory.Exists(Path.Combine(fixture.Root, "cache"))
            ? Directory.GetFiles(Path.Combine(fixture.Root, "cache"), "*", SearchOption.AllDirectories) : []);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 15)]
    public async Task RetainedOriginal_ColdPartialReturnsImmediatelyAndCoalescesOneCompleteFill(int start, int end)
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        Task<HttpResponseMessage> Full(CancellationToken token) => OpenFullAsync(token);
        async Task<HttpResponseMessage> OpenFullAsync(CancellationToken token)
        {
            Interlocked.Increment(ref opens);
            opening.TrySetResult();
            await release.Task.WaitAsync(token);
            return fixture.Response();
        }
        using var one = fixture.Response(fixture.Bytes[start..(end + 1)]);
        one.StatusCode = HttpStatusCode.PartialContent;
        one.Content.Headers.ContentRange = new(start, end, fixture.Bytes.Length);
        using var two = fixture.Response(fixture.Bytes[(start + 2)..(end + 3)]);
        two.StatusCode = HttpStatusCode.PartialContent;
        two.Content.Headers.ContentRange = new(start + 2, end + 2, fixture.Bytes.Length);
        try
        {
            await fixture.WrapAsync(fixture.Stream(one) with { OpenOriginalResponseAsync = Full });
            await fixture.WrapAsync(fixture.Stream(two) with { OpenOriginalResponseAsync = Full });
            Assert.Equal(fixture.Bytes[start..(end + 1)], await one.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(fixture.Bytes[(start + 2)..(end + 3)], await two.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(release.Task.IsCompleted);
            await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref opens));
            Assert.Empty(await fixture.Mappings.ListCacheMappingsAsync());
            one.Dispose();
            two.Dispose();
            fixture.Clock.Advance(TimeSpan.FromSeconds(11));
            release.TrySetResult();
            await fixture.Cache.TransferCompletionAsync(fixture.Track, fixture.OriginalScope, 7, originalDownload: true).WaitAsync(TimeSpan.FromSeconds(5));
            var mapping = Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
            Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(mapping.LocalPath));
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("short")]
    [InlineData("oversized")]
    [InlineData("failure")]
    public async Task RetainedOriginal_InvalidCompleteFillNeverPublishesTheSuccessfulClientRange(string failure)
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var response = fixture.Response(fixture.Bytes[10..16]);
        response.StatusCode = HttpStatusCode.PartialContent;
        response.Content.Headers.ContentRange = new(10, 15, fixture.Bytes.Length);
        await fixture.WrapAsync(fixture.Stream(response) with
        {
            OpenOriginalResponseAsync = _ =>
            {
                opened.TrySetResult();
                if (failure == "failure") throw new IOException("Fixture source failed.");
                var full = fixture.Response(failure == "oversized" ? fixture.Bytes : fixture.Bytes[..8]);
                if (failure == "partial")
                {
                    full.StatusCode = HttpStatusCode.PartialContent;
                    full.Content.Headers.ContentRange = new(0, 7, fixture.Bytes.Length);
                }
                else full.Content.Headers.ContentLength = failure == "oversized" ? fixture.Bytes.Length - 1 : fixture.Bytes.Length;
                return Task.FromResult(full);
            }
        });
        Assert.Equal(fixture.Bytes[10..16], await response.Content.ReadAsByteArrayAsync());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Cache.TransferCompletionAsync(fixture.Track, fixture.OriginalScope, 7, originalDownload: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await fixture.Mappings.ListCacheMappingsAsync());
        await fixture.RestartCacheAsync();
        Assert.Empty(Directory.Exists(Path.Combine(fixture.Root, "cache"))
            ? Directory.GetFiles(Path.Combine(fixture.Root, "cache"), "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task RetainedOriginal_FullFillSurvivesAClientGapLongerThanThePlaybackSeekGrace()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var source = new ControlledSource(fixture.Bytes[..16], shortBody: false, fail: false);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        response.Content.Headers.ContentLength = 16;
        response.Content.Headers.ContentType = new("audio/flac");
        await fixture.WrapAsync(fixture.Stream(response, ranges: false));
        var reader = await response.Content.ReadAsStreamAsync();
        Assert.Equal(0, reader.ReadByte());
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        response.Dispose();
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False(source.Closed);
        source.Release.TrySetResult();
        await fixture.Cache.TransferCompletionAsync(fixture.Track, fixture.OriginalScope, 7, originalDownload: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
    }

    [Fact]
    public async Task RetainedOriginal_ShutdownCancelsTheOwnedFillWithoutPublishingPartialBytes()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var source = new ControlledSource(fixture.Bytes[..16], shortBody: false, fail: false);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        response.Content.Headers.ContentLength = 16;
        response.Content.Headers.ContentType = new("audio/flac");
        await fixture.WrapAsync(fixture.Stream(response, ranges: false));
        var reader = await response.Content.ReadAsStreamAsync();
        Assert.Equal(0, reader.ReadByte());
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Cache.DisposeAsync();
        Assert.True(source.Closed);
        Assert.Empty(await fixture.Mappings.ListCacheMappingsAsync());
        Assert.Empty(Directory.Exists(Path.Combine(fixture.Root, "cache"))
            ? Directory.GetFiles(Path.Combine(fixture.Root, "cache"), "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task RetainedOriginal_IsIndexedWithThreeDayExpiryAndUsesTheExistingAudioBudget()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        fixture.MaximumBytes = fixture.Bytes.Length;
        using (var first = fixture.Response())
        {
            await fixture.WrapAsync(fixture.Stream(first));
            await first.Content.CopyToAsync(Stream.Null);
        }
        var saved = Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
        var controller = new DownloadsController(NullLogger<DownloadsController>.Instance, fixture.Configuration, contextFactory: fixture.Contexts)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var inventory = Assert.IsType<OkObjectResult>(await controller.GetDownloads("cache"));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(inventory.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var entry = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal("Indexed", entry.GetProperty("publicationState").GetString());
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(72).UtcDateTime, entry.GetProperty("expiresAt").GetDateTime());
        using (var second = fixture.Response())
        {
            await fixture.WrapAsync(fixture.Stream(second) with { ServingExternalId = "second-original" });
            await second.Content.CopyToAsync(Stream.Null);
        }
        Assert.False(File.Exists(saved.LocalPath));
        var replacement = Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
        fixture.Clock.Advance(TimeSpan.FromHours(72) - TimeSpan.FromSeconds(1));
        await fixture.Cache.CleanupExpiredAudioAsync(TimeSpan.FromHours(72));
        Assert.True(File.Exists(replacement.LocalPath));
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Cache.CleanupExpiredAudioAsync(TimeSpan.FromHours(72));
        Assert.False(File.Exists(replacement.LocalPath));
        Assert.Empty(await fixture.Mappings.ListCacheMappingsAsync());
    }
}
