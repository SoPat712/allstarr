using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

// Duration fields describe the timeline; they never establish byte-range support.
internal static class StreamDurationGuidance
{
    private const int PrefixLimit = 1024 * 1024;

    public static void Attach(ProtocolProviderStream routed, Func<Task<Song?>> metadata, HttpResponse response)
    {
        if (routed.IsCached || routed.Lease.SupportsByteRanges || routed.Response.StatusCode != HttpStatusCode.OK)
            return;
        var container = routed.Lease.Media.Container.ToLowerInvariant();
        if (container is not ("flac" or "mp3" or "mp4" or "m4a")) return;
        var original = routed.Response.Content;
        var replacement = new StreamContent(new DeferredGuidanceStream(original, async cancellationToken =>
        {
            await response.StartAsync(cancellationToken);
            return await PrepareAsync(original, routed, container, metadata, cancellationToken);
        }));
        foreach (var header in original.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        // An optional FLAC ID3 prelude is removed lazily, so its final length is not known at header time.
        if (container == "flac") replacement.Headers.ContentLength = null;
        routed.Response.Content = replacement;
    }

    private static async Task<Stream> PrepareAsync(HttpContent original, ProtocolProviderStream routed, string container,
        Func<Task<Song?>> metadata, CancellationToken cancellationToken)
    {
        var source = await original.ReadAsStreamAsync(cancellationToken);
        using var prefix = new MemoryStream();
        var skip = 0;
        var usable = false;
        if (container is "flac" or "mp3")
        {
            await ReadUntilAsync(source, prefix, 10, cancellationToken);
            var tagLength = MediaDurationHeaders.TryGetId3TagLength(prefix.GetBuffer().AsSpan(0, (int)prefix.Length), out var tag)
                ? tag : 0;
            await ReadUntilAsync(source, prefix, tagLength + (container == "flac" ? 42 : 1441), cancellationToken);
            if (container == "flac" && prefix.Length >= tagLength + 4 &&
                prefix.GetBuffer().AsSpan(tagLength, 4).SequenceEqual("fLaC"u8))
            {
                skip = tagLength;
                usable = prefix.Length >= tagLength + 42;
            }
            else if (container == "mp3") usable = prefix.Length >= tagLength + 4;
        }
        else
        {
            var foundMoov = false;
            while (prefix.Length < PrefixLimit)
            {
                var offset = (int)prefix.Length;
                await ReadUntilAsync(source, prefix, offset + 8, cancellationToken);
                if (prefix.Length < offset + 8) break;
                var bytes = prefix.GetBuffer();
                var size = (ulong)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                var type = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4, 4));
                var headerLength = 8;
                if (size == 1)
                {
                    await ReadUntilAsync(source, prefix, offset + 16, cancellationToken);
                    if (prefix.Length < offset + 16) break;
                    bytes = prefix.GetBuffer();
                    size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8));
                    headerLength = 16;
                }
                if (type == 0x6d646174) { usable = foundMoov; break; }
                if (type == 0x6d6f6f66 || size < (ulong)headerLength || size > (ulong)(PrefixLimit - offset)) break;
                await ReadUntilAsync(source, prefix, offset + (int)size, cancellationToken);
                if (prefix.Length < offset + (int)size) break;
                if (type == 0x6d6f6f76) foundMoov = true;
            }
        }

        var prepared = prefix.ToArray();
        if (usable)
        {
            TimeSpan? duration = null;
            if (routed.Response.Headers.TryGetValues("X-Allstarr-Duration-Seconds", out var values))
            {
                var headers = values.ToArray();
                if (headers.Length == 1 && double.TryParse(headers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                    double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds - 1)
                    duration = TimeSpan.FromSeconds(seconds);
            }
            if (duration == null)
            {
                try
                {
                    var song = await metadata();
                    duration = song?.PreciseDuration ?? (song?.Duration is > 0 ? TimeSpan.FromSeconds(song.Duration.Value) : null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { /* Metadata failure must not interrupt a valid audio response. */ }
            }
            if (duration.HasValue) MediaDurationHeaders.TryPatch(prepared.AsSpan(skip), container, duration.Value);
        }
        return new PrefetchedStream(source, original, prepared.AsMemory(skip));
    }

    private static async Task ReadUntilAsync(Stream source, MemoryStream prefix, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(8192, length - (int)prefix.Length)];
        while (prefix.Length < length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length - (int)prefix.Length)), cancellationToken);
            if (read == 0) break;
            prefix.Write(buffer, 0, read);
        }
    }

    private sealed class DeferredGuidanceStream(HttpContent owner, Func<CancellationToken, Task<Stream>> prepare) : Stream
    {
        private Task<Stream>? prepared;
        private bool disposed;
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            var stream = await (prepared ??= prepare(cancellationToken));
            return await stream.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
