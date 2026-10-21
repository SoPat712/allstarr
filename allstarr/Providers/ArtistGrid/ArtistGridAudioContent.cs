using System.Net;

namespace allstarr.Core.Providers.ArtistGrid;

internal sealed class ArtistGridAudioContent : HttpContent
{
    internal const long MaximumBytes = 1024L * 1024 * 1024;
    private readonly HttpContent _inner;
    private readonly long _maximumBytes;

    internal ArtistGridAudioContent(HttpContent inner, long maximumBytes = MaximumBytes)
    {
        _inner = inner;
        _maximumBytes = maximumBytes;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength ?? 0;
        return _inner.Headers.ContentLength.HasValue;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var bounded = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
        await bounded.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new AudioReadStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _maximumBytes);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private sealed class AudioReadStream(Stream inner, long maximumBytes) : Stream
    {
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromMinutes(10));
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, maximumBytes - _read + 1)],
                timeout.Token).ConfigureAwait(false);
            _read += count;
            if (_read > maximumBytes) throw new HttpRequestException("The provider audio file exceeds the size limit.");
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lifetime.Dispose();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
