namespace allstarr.Core.Protocols;

// Verify the first byte before committing response headers, then replay it without buffering the song.
internal sealed class PrefetchedStream(Stream source, HttpContent owner, byte first) : Stream
{
    private bool pending = true;
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public static async Task<bool> PrepareAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = response.Content;
        var source = await content.ReadAsStreamAsync(cancellationToken);
        var prefix = new byte[1];
        if (await source.ReadAsync(prefix, cancellationToken) == 0) return false;
        var replacement = new StreamContent(new PrefetchedStream(source, content, prefix[0]));
        foreach (var header in content.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var destination = buffer.AsSpan(offset, count);
        if (destination.IsEmpty) return 0;
        if (!pending) return source.Read(buffer, offset, count);
        destination[0] = first;
        pending = false;
        return 1;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return ValueTask.FromResult(0);
        if (!pending) return source.ReadAsync(buffer, cancellationToken);
        buffer.Span[0] = first;
        pending = false;
        return ValueTask.FromResult(1);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) owner.Dispose();
        base.Dispose(disposing);
    }
}
