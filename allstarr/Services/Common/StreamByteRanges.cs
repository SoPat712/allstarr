using System.Net;
using System.Net.Http.Headers;

namespace allstarr.Services.Common;

internal static class StreamByteRanges
{
    public static RangeHeaderValue? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!RangeHeaderValue.TryParse(value, out var range) || !IsSingleByteRange(range))
            throw new HttpRequestException("Only one valid byte range may be requested.", null,
                HttpStatusCode.RequestedRangeNotSatisfiable);
        return range;
    }

    public static bool IsSingleByteRange(RangeHeaderValue range) =>
        range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) && range.Ranges.Count == 1 &&
        (range.Ranges.Single().From.HasValue || range.Ranges.Single().To > 0);

    public static bool TryResolve(RangeHeaderValue request, long length, out long start, out long end)
    {
        start = end = 0;
        if (!IsSingleByteRange(request) || length <= 0) return false;
        var range = request.Ranges.Single();
        start = range.From ?? Math.Max(0, length - range.To!.Value);
        end = range.From.HasValue ? Math.Min(range.To ?? length - 1, length - 1) : length - 1;
        return start < length && end >= start;
    }

    public static bool HasValidContentRange(ContentRangeHeaderValue? range, long? contentLength) =>
        range is { HasRange: true, From: { } from, To: { } to } &&
        range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) && from >= 0 && to >= from &&
        to - from < long.MaxValue && (!range.Length.HasValue || range.Length > to) &&
        (!contentLength.HasValue || contentLength == to - from + 1);

    public static bool Matches(HttpResponseMessage response, RangeHeaderValue? request)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent)
            return request == null || request.Ranges.Single().From == 0;
        var actual = response.Content.Headers.ContentRange;
        if (request == null || !HasValidContentRange(actual, response.Content.Headers.ContentLength)) return false;
        if (actual!.Length is { } length)
            return TryResolve(request, length, out var start, out var end) && actual.From == start && actual.To == end;
        var requested = request.Ranges.Single();
        return requested.From.HasValue && actual.From == requested.From &&
            (!requested.To.HasValue || actual.To == requested.To);
    }
}
