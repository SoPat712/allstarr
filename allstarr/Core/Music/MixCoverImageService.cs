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

    public MixCoverImageService() : this(MixCoverRenderer.Render) { }

    public MixCoverImageService(MixCoverArtLoader art) : this(MixCoverRenderer.Render, art.LoadAsync) { }

    public MixCoverImageService(MixCoverArtLoader art, PersonalMixService mixes, IMediaAssetResolver assets)
        : this(MixCoverRenderer.Render, art.LoadAsync, serviceArt: (input, owner, token) => mixes.LoadServiceArtworkAsync(input, owner, assets, token)) { }

    internal MixCoverImageService(Func<MixCoverInput, byte[]> render, Func<string, CancellationToken, Task<byte[]?>>? loadArt = null,
        int maxEntries = 96, long maxBytes = 24 * 1024 * 1024,
        Func<MixCoverInput, Guid, CancellationToken, Task<ResolvedMediaAsset?>>? serviceArt = null)
    {
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
    public async Task<IActionResult> CreateResponseAsync(MixCoverInput input, HttpContext context, string? version = null, Guid? owner = null)
    {
        if (input.ServiceArtwork != null)
        {
            var asset = owner.HasValue && _serviceArt != null ? await _serviceArt(input, owner.Value, context.RequestAborted) : null;
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
        if (noArtwork && input.Count > 0)
        {
            context.Response.Headers.CacheControl = "private, max-age=60";
            return new FileContentResult(_render(input), "image/png");
        }
        if (noArtwork && ImageConditionalRequestHelper.MatchesIfNoneMatch(context.Request.Headers, etag))
        {
            SetCaching(context, etag, versioned);
            return new StatusCodeResult(StatusCodes.Status304NotModified);
        }
        Task<(byte[] Bytes, string ContentType, bool Complete)> work;
        lock (_gate)
        {
            if (!_images.TryGetValue(tag, out var cached) && noArtwork)
                cached = Remember(tag, (_render(input), "image/png"));
            if (cached.Bytes != null)
            {
                SetCaching(context, etag, versioned);
                if (ImageConditionalRequestHelper.MatchesIfNoneMatch(context.Request.Headers, etag))
                    return new StatusCodeResult(StatusCodes.Status304NotModified);
                return new FileContentResult(cached.Bytes, cached.ContentType);
            }
            // Requests for one collage share its picture downloads.
            if (!_pending.TryGetValue(tag, out work!))
                _pending[tag] = work = RenderCollageAsync(input);
        }
        var (bytes, contentType, complete) = await work;
        lock (_gate)
        {
            if (_pending.TryGetValue(tag, out var current) && current == work) _pending.Remove(tag);
            if (complete && !_images.ContainsKey(tag)) Remember(tag, (bytes, contentType));
        }
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
