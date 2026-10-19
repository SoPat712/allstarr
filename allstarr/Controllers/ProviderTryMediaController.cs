using System.Net;
using System.Net.Http.Headers;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/providers/try/media")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ProviderTryMediaController(
    ProviderTryMediaStore media,
    ILogger<ProviderTryMediaController> logger) : ControllerBase
{
    [HttpGet("{ticket}")]
    public async Task<IActionResult> Get(string ticket, [FromQuery] string? download = null)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession { IsAdministrator: true, AllstarrUserId: { } userId } || userId == Guid.Empty)
        {
            logger.LogWarning("Blocked protected provider media due to missing administrator identity");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator permissions required" });
        }

        var entry = media.Find(userId, ticket);
        if (entry == null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        RangeHeaderValue? range;
        try { range = StreamByteRanges.Parse(Request.Headers.Range.ToString()); }
        catch (HttpRequestException) { return Unsatisfiable(entry.Length); }
        if (range != null && entry.Length is { } knownLength &&
            !StreamByteRanges.TryResolve(range, knownLength, out _, out _))
            return Unsatisfiable(knownLength);

        HttpResponseMessage upstream;
        try { upstream = await entry.OpenAsync(range?.ToString(), HttpContext.RequestAborted); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning("Protected provider media could not be opened ({ExceptionType})", exception.GetType().Name);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Media is unavailable" });
        }

        var totalLength = entry.Length ?? upstream.Content.Headers.ContentRange?.Length;
        if (upstream.StatusCode == HttpStatusCode.OK) totalLength ??= upstream.Content.Headers.ContentLength;
        if (upstream.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            upstream.Dispose();
            return Unsatisfiable(totalLength);
        }
        if (upstream.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent) ||
            !StreamByteRanges.Matches(upstream, range))
        {
            upstream.Dispose();
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Media is unavailable" });
        }

        long skip = 0;
        long? count = upstream.Content.Headers.ContentLength;
        var status = (int)upstream.StatusCode;
        string? contentRange = upstream.Content.Headers.ContentRange?.ToString();
        // A player's opening request ("bytes=0-") on a stream of unknown length gets the whole song.
        if (range != null && upstream.StatusCode == HttpStatusCode.OK && totalLength == null &&
            range.Ranges.Count == 1 && range.Ranges.Single() is { From: 0, To: null })
            range = null;
        if (range != null && upstream.StatusCode == HttpStatusCode.OK)
        {
            if (totalLength is not { } length || !StreamByteRanges.TryResolve(range, length, out var start, out var end))
            {
                upstream.Dispose();
                return Unsatisfiable(totalLength);
            }
            skip = start;
            count = end - start + 1;
            contentRange = new ContentRangeHeaderValue(start, end, length).ToString();
            status = StatusCodes.Status206PartialContent;
        }
        else if (upstream.StatusCode == HttpStatusCode.OK) count = totalLength;

        Response.RegisterForDispose(upstream);
        return new MediaResult(upstream, entry.ContentType, entry.FileName, download == "1", status,
            totalLength.HasValue, contentRange, skip, count);
    }

    private IActionResult Unsatisfiable(long? length)
    {
        if (length.HasValue)
        {
            Response.Headers.AcceptRanges = "bytes";
            Response.Headers.ContentRange = $"bytes */{length.Value}";
        }
        Response.ContentLength = 0;
        return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
    }

    private sealed class MediaResult(HttpResponseMessage upstream, string contentType, string fileName,
        bool download, int status, bool ranges, string? contentRange, long skip, long? count) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            using (upstream)
            {
                var response = context.HttpContext.Response;
                var token = context.HttpContext.RequestAborted;
                response.StatusCode = status;
                response.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var parsedType)
                    ? parsedType.ToString() : "application/octet-stream";
                response.ContentLength = count;
                if (ranges) response.Headers.AcceptRanges = "bytes";
                if (contentRange != null) response.Headers.ContentRange = contentRange;
                var safeName = new string(Path.GetFileName(fileName.Replace('\\', '/'))
                    .Where(character => !char.IsControl(character)).ToArray());
                response.Headers.ContentDisposition = new ContentDispositionHeaderValue(download ? "attachment" : "inline")
                { FileNameStar = string.IsNullOrWhiteSpace(safeName) ? "media" : safeName }.ToString();
                var input = await upstream.Content.ReadAsStreamAsync(token);
                var buffer = new byte[65536];
                var remainingSkip = skip;
                if (remainingSkip > 0 && input.CanSeek) input.Seek(remainingSkip, SeekOrigin.Current);
                else
                {
                    while (remainingSkip > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remainingSkip)), token);
                        if (read == 0) throw new IOException("Media ended before the requested range.");
                        remainingSkip -= read;
                    }
                }
                if (count is not { } remaining)
                {
                    await input.CopyToAsync(response.Body, token);
                    return;
                }
                while (remaining > 0)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                    if (read == 0) throw new IOException("Media ended before its declared length.");
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), token);
                    remaining -= read;
                }
            }
        }
    }
}
