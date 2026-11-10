using allstarr.Core.Operations;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Music;

public sealed class MixCoverImageService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _images = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, Task<(byte[] Bytes, string ContentType, bool Complete)>> _pending = new(StringComparer.Ordinal);
    private readonly Func<MixCoverInput, byte[]> _render;
    private readonly Func<string, CancellationToken, Task<byte[]?>>? _loadArt;
    private readonly Func<MixCoverInput, Guid, CancellationToken, Task<ResolvedMediaAsset?>>? _serviceArt;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _bytes;
    private readonly IApplicationCache? _cache;
    private readonly SemaphoreSlim _warmup = new(1, 1);

    public MixCoverImageService() : this(MixCoverRenderer.Render) { }

    public MixCoverImageService(MixCoverArtLoader art) : this(MixCoverRenderer.Render, art.LoadAsync) { }

    public MixCoverImageService(MixCoverArtLoader art, IServiceProvider services, IApplicationCache cache)
        : this(MixCoverRenderer.Render, art.LoadAsync, cache: cache,
            serviceArt: (input, owner, token) => services.GetRequiredService<PersonalMixService>()
                .LoadServiceArtworkAsync(input, owner, services.GetRequiredService<IMediaAssetResolver>(), token))
    { }

    internal MixCoverImageService(Func<MixCoverInput, byte[]> render, Func<string, CancellationToken, Task<byte[]?>>? loadArt = null,
        int maxEntries = 96, long maxBytes = 24 * 1024 * 1024,
        Func<MixCoverInput, Guid, CancellationToken, Task<ResolvedMediaAsset?>>? serviceArt = null, IApplicationCache? cache = null)
    {
        _cache = cache;
        _render = render;
        _serviceArt = serviceArt;
        _loadArt = loadArt;
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    public static string GetTag(MixCoverInput input) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Input = input, Renderer = "collage-v4" }))) +
        "-collage-v4";

    // A short form of the tag that apps can put in the cover URL.
    public static string Version(MixCoverInput input) => GetTag(input)[..16].ToLowerInvariant();

    // Service covers stay intact. Otherwise show song covers or a gradient. A collage missing a picture has no validator,
    // so apps ask again later instead of keeping it.
    public async Task<IActionResult> CreateResponseAsync(MixCoverInput input, HttpContext context, string? version = null, Guid? owner = null, string? cacheVariant = null)
    {
        if (input.ServiceArtwork != null)
        {
            var key = CacheKey(input, context, owner, cacheVariant);
            MediaAssetSource? asset;
            try { asset = owner.HasValue && _serviceArt != null ? await LoadServiceAsync(input, owner.Value, key) : null; }
            catch (TimeoutException)
            {
                context.Response.Headers.CacheControl = "private, max-age=60";
                return new FileContentResult(_render(input), "image/png");
            }
            if (asset == null)
            {
                context.Response.Headers.CacheControl = "private, no-store";
                return new NotFoundResult();
            }
            var serviceTag = ImageConditionalRequestHelper.ComputeStrongETag(asset.Bytes);
            context.Response.Headers.CacheControl = "private, max-age=300";
            context.Response.Headers.ETag = serviceTag;
            return ImageConditionalRequestHelper.MatchesIfNoneMatch(context.Request.Headers, serviceTag)
                ? new StatusCodeResult(StatusCodes.Status304NotModified) : new FileContentResult(asset.Bytes, asset.ContentType);
        }
        var tag = GetTag(input);
        var etag = $"\"{tag}\"";
        var versioned = version == Version(input);
        var noArtwork = input.Art is not { Count: > 0 } || _loadArt == null;
        if (noArtwork && input.Count == 0 && ImageConditionalRequestHelper.MatchesIfNoneMatch(context.Request.Headers, etag))
        {
            SetCaching(context, etag, versioned);
            return new StatusCodeResult(StatusCodes.Status304NotModified);
        }
        var cacheKey = CacheKey(input, context, variant: cacheVariant);
        Task<(byte[] Bytes, string ContentType, bool Complete)> work;
        bool shared;
        lock (_gate)
        {
            if (_cache == null && _images.TryGetValue(cacheKey, out var cached))
            {
                SetCaching(context, etag, versioned);
                if (ImageConditionalRequestHelper.MatchesIfNoneMatch(context.Request.Headers, etag))
                    return new StatusCodeResult(StatusCodes.Status304NotModified);
                return new FileContentResult(cached.Bytes, cached.ContentType);
            }
            // Requests for one collage share its picture downloads.
            shared = _pending.TryGetValue(cacheKey, out work!);
            if (!shared)
                _pending[cacheKey] = work = LoadOrRenderAsync(input, cacheKey, noArtwork);
        }
        (byte[] bytes, string contentType, bool complete) result;
        try
        {
            using (shared ? RequestTiming.Current?.Stage("lock") : null) result = await work;
            lock (_gate)
                if (_cache == null && result.complete && !_images.ContainsKey(cacheKey)) Remember(cacheKey, (result.bytes, result.contentType));
        }
        finally
        {
            lock (_gate)
                if (_pending.TryGetValue(cacheKey, out var current) && current == work) _pending.Remove(cacheKey);
        }
        var (bytes, contentType, complete) = result;
        if (!complete)
        {
            context.Response.Headers.CacheControl = "private, max-age=60";
            return new FileContentResult(bytes, contentType);
        }
        SetCaching(context, etag, versioned);
        if (ImageConditionalRequestHelper.MatchesIfNoneMatch(context.Request.Headers, etag))
            return new StatusCodeResult(StatusCodes.Status304NotModified);
        return new FileContentResult(bytes, contentType);
    }

    private static string CacheKey(MixCoverInput input, HttpContext context, Guid? owner = null, string? variant = null) =>
        CacheKeyBuilder.BuildImageVariantKey(GetTag(input), owner, variant,
            context.Request.Query["tag"], context.Request.Query["maxWidth"], context.Request.Query["maxHeight"],
            context.Request.Query["fillWidth"], context.Request.Query["fillHeight"], context.Request.Query["width"],
            context.Request.Query["height"], context.Request.Query["quality"], context.Request.Query["format"]);

    private async Task<(byte[] Bytes, string ContentType, bool Complete)> LoadOrRenderAsync(MixCoverInput input, string key, bool noArtwork)
    {
        if (_cache != null && await _cache.GetAsync<MediaAssetSource>(key) is { } cached)
            return (cached.Bytes, cached.ContentType, !noArtwork || input.Count == 0);
        var image = noArtwork ? (_render(input), "image/png", input.Count == 0) : await RenderCollageAsync(input);
        if ((image.Item3 || noArtwork) && _cache != null)
            await _cache.SetAsync(key, new MediaAssetSource(image.Item1, image.Item2),
                image.Item3 ? TimeSpan.FromDays(365) : TimeSpan.FromSeconds(60));
        return image;
    }

    private async Task<MediaAssetSource?> LoadServiceAsync(MixCoverInput input, Guid owner, string key)
    {
        Task<(byte[] Bytes, string ContentType, bool Complete)> work;
        bool shared;
        lock (_gate)
        {
            shared = _pending.TryGetValue(key, out work!);
            if (!shared)
                _pending[key] = work = Fetch();
        }
        try
        {
            using var waiting = shared ? RequestTiming.Current?.Stage("lock") : null;
            var image = await work;
            return image.Complete ? new(image.Bytes, image.ContentType) : null;
        }
        finally
        {
            lock (_gate)
                if (_pending.TryGetValue(key, out var current) && current == work) _pending.Remove(key);
        }

        async Task<(byte[], string, bool)> Fetch()
        {
            if (_cache != null && await _cache.GetAsync<MediaAssetSource>(key) is { } cached)
                return (cached.Bytes, cached.ContentType, true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ResolvedMediaAsset? asset;
            try { asset = await _serviceArt!(input, owner, timeout.Token).WaitAsync(timeout.Token); }
            catch (OperationCanceledException exception) { throw new TimeoutException("Mix artwork lookup timed out.", exception); }
            if (asset == null) return ([], "", false);
            if (_cache != null)
                await _cache.SetAsync(key, new MediaAssetSource(asset.Bytes, asset.ContentType), TimeSpan.FromDays(365));
            return (asset.Bytes, asset.ContentType, true);
        }
    }

    public Task WarmAsync(IEnumerable<MixCoverInput> inputs, Guid owner) => Task.Run(async () =>
    {
        foreach (var input in inputs)
        {
            await _warmup.WaitAsync();
            try
            {
                await Task.Delay(100);
                await CreateResponseAsync(input, new DefaultHttpContext(), Version(input), owner);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            finally { _warmup.Release(); }
        }
    });

    private (byte[] Bytes, string ContentType) Remember(string tag, (byte[] Bytes, string ContentType) image)
    {
        if (image.Bytes.LongLength > _maxBytes) return image;
        while (_images.Count >= _maxEntries || _bytes + image.Bytes.LongLength > _maxBytes)
        {
            var oldest = _order.Dequeue();
            _bytes -= _images[oldest].Bytes.LongLength;
            _images.Remove(oldest);
        }
        _images.Add(tag, image);
        _order.Enqueue(tag);
        _bytes += image.Bytes.LongLength;
        return image;
    }

    private async Task<(byte[] Bytes, string ContentType, bool Complete)> RenderCollageAsync(MixCoverInput input)
    {
        var pictures = await Task.WhenAll(input.Art!.Select(reference => _loadArt!(reference, CancellationToken.None)));
        if (pictures.All(picture => picture != null) && MixCoverRenderer.RenderCollage(pictures!) is { } collage)
            return (collage, "image/jpeg", true);
        // Whatever loaded still beats a gradient for now; the full cover follows on a later request.
        var loaded = pictures.Where(picture => picture != null).Take(1).ToArray();
        return loaded.Length == 1 && MixCoverRenderer.RenderCollage(loaded!) is { } single
            ? (single, "image/jpeg", false)
            : (_render(input), "image/png", false);
    }

    // A URL naming the current version never changes, so apps can keep it; any other request revalidates.
    private static void SetCaching(HttpContext context, string etag, bool versioned)
    {
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = versioned
            ? "private, max-age=31536000, immutable" : "private, max-age=0, must-revalidate";
    }
}
