using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Music;

public sealed class MixCoverImageService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Func<MixCoverInput, byte[]> _render;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _bytes;

    public MixCoverImageService() : this(MixCoverRenderer.Render) { }

    internal MixCoverImageService(Func<MixCoverInput, byte[]> render, int maxEntries = 64, long maxBytes = 16 * 1024 * 1024)
    {
        _render = render;
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    public static string GetTag(MixCoverInput input) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input)));

    public IActionResult CreateResponse(MixCoverInput input, HttpContext context)
    {
        var tag = GetTag(input);
        var etag = $"\"{tag}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
        if (ImageConditionalRequestHelper.MatchesIfNoneMatch(context.Request.Headers, etag))
            return new StatusCodeResult(StatusCodes.Status304NotModified);
        lock (_gate)
        {
            if (!_images.TryGetValue(tag, out var image))
            {
                image = _render(input);
                if (image.LongLength <= _maxBytes)
                {
                    while (_images.Count >= _maxEntries || _bytes + image.LongLength > _maxBytes)
                    {
                        var oldest = _order.Dequeue();
                        _bytes -= _images[oldest].LongLength;
                        _images.Remove(oldest);
                    }
                    _images.Add(tag, image);
                    _order.Enqueue(tag);
                    _bytes += image.LongLength;
                }
            }
            return new FileContentResult(image, "image/png");
        }
    }
}
