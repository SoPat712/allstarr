using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Monochrome;

internal static class MonochromeAudioFormat
{
    private const int WindowBytes = 64 * 1024;

    public static async Task<ProviderMediaFormat?> ReadAsync(
        Func<RangeHeaderValue, CancellationToken, Task<HttpResponseMessage>> open, CancellationToken token)
    {
        long offset = 0;
        var fileType = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var range = new RangeHeaderValue(offset, offset + WindowBytes - 1);
            using var response = await open(range, timeout.Token);
            if (!response.IsSuccessStatusCode || !StreamByteRanges.Matches(response, range) ||
                offset > 0 && response.StatusCode != HttpStatusCode.PartialContent) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[WindowBytes];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token);
                if (read == 0) break;
                length += read;
            }
            var cursor = 0;
            while (cursor + 8 <= length)
            {
                var size = Size(bytes.AsSpan(cursor, length - cursor), out var header);
                if (size < header || size > 2L * 1024 * 1024 * 1024) return null;
                var type = bytes.AsSpan(cursor + 4, 4);
                if (type.SequenceEqual("ftyp"u8)) fileType = true;
                if (fileType && type.SequenceEqual("moov"u8))
                {
                    if (size > length - cursor)
                    {
                        if (size > 256 * 1024) return null;
                        var movieRange = new RangeHeaderValue(offset, offset + size - 1);
                        using var movie = await open(movieRange, timeout.Token);
                        if (!movie.IsSuccessStatusCode || !StreamByteRanges.Matches(movie, movieRange) ||
                            offset > 0 && movie.StatusCode != HttpStatusCode.PartialContent) return null;
                        await using var movieStream = await movie.Content.ReadAsStreamAsync(timeout.Token);
                        bytes = new byte[(int)size];
                        await movieStream.ReadExactlyAsync(bytes, timeout.Token);
                        cursor = 0;
                    }
                    var codec = Codec(bytes.AsSpan(cursor + header, (int)size - header), 0);
                    return codec == null ? null : new("audio/mp4", "mp4", codec);
                }
                offset += size;
                if (size > length - cursor) break;
                cursor += (int)size;
            }
            if (cursor + 8 > length) return null;
        }
        return null;
    }

    private static long Size(ReadOnlySpan<byte> box, out int header)
    {
        header = 8;
        var size = BinaryPrimitives.ReadUInt32BigEndian(box);
        if (size != 1) return size;
        header = 16;
        if (box.Length < header) return 0;
        var extended = BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
        return extended > long.MaxValue ? 0 : (long)extended;
    }

    private static string? Codec(ReadOnlySpan<byte> boxes, int depth)
    {
        if (depth > 6) return null;
        while (boxes.Length >= 8)
        {
            var size = Size(boxes, out var header);
            if (size < header || size > boxes.Length) return null;
            var type = boxes.Slice(4, 4);
            var body = boxes.Slice(header, (int)size - header);
            if (type.SequenceEqual("stsd"u8))
            {
                if (body.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(body[4..]) != 1) return null;
                var entry = body[8..];
                var entrySize = Size(entry, out var entryHeader);
                if (entrySize < entryHeader + 28 || entrySize > entry.Length) return null;
                return entry.Slice(4, 4).SequenceEqual("alac"u8) ? "alac" :
                    entry.Slice(4, 4).SequenceEqual("mp4a"u8) ? "aac" : null;
            }
            if (type.SequenceEqual("trak"u8) || type.SequenceEqual("mdia"u8) ||
                type.SequenceEqual("minf"u8) || type.SequenceEqual("stbl"u8))
            {
                var codec = Codec(body, depth + 1);
                if (codec != null) return codec;
            }
            boxes = boxes[(int)size..];
        }
        return null;
    }
}
