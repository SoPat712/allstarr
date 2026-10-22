namespace allstarr.Core.Protocols;

// Verify a bounded prefix before committing response headers, then replay it without buffering the song.
internal sealed class PrefetchedStream(Stream source, HttpContent owner, ReadOnlyMemory<byte> prefix) : Stream
{
    private int position;
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public static async Task<bool> PrepareAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = response.Content;
        var source = await content.ReadAsStreamAsync(cancellationToken);
        var prefix = new byte[4096];
        var count = await source.ReadAsync(prefix, cancellationToken);
        if (count == 0) return false;
        var start = content.Headers.ContentRange?.From;
        var size = content.Headers.ContentRange?.Length ?? content.Headers.ContentLength;
        var facts = start is null or 0 ? allstarr.Services.Common.AudioMediaFacts.ReadPrefix(prefix.AsSpan(0, count), size) : null;
        while (start is null or 0 && facts == null && count < prefix.Length &&
            allstarr.Services.Common.AudioMediaFacts.IsPartialHeader(prefix.AsSpan(0, count)))
        {
            var read = await source.ReadAsync(prefix.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
            facts = allstarr.Services.Common.AudioMediaFacts.ReadPrefix(prefix.AsSpan(0, count), size);
        }
        var replacement = new PreparedContent(new PrefetchedStream(source, content, prefix.AsMemory(0, count)), facts);
        foreach (var header in content.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
        if (facts != null) replacement.Headers.ContentType = new(facts.Media.MimeType);
        return true;
    }

    internal static allstarr.Core.Music.TrackMediaFacts? Facts(HttpContent content) => (content as PreparedContent)?.Facts;

    private sealed class PreparedContent(Stream stream, allstarr.Core.Music.TrackMediaFacts? facts) : StreamContent(stream)
    {
        internal allstarr.Core.Music.TrackMediaFacts? Facts { get; } = facts;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var destination = buffer.AsSpan(offset, count);
        if (destination.IsEmpty) return 0;
        if (position == prefix.Length) return source.Read(buffer, offset, count);
        var read = Math.Min(count, prefix.Length - position);
        prefix.Span.Slice(position, read).CopyTo(destination);
        position += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return ValueTask.FromResult(0);
        if (position == prefix.Length) return source.ReadAsync(buffer, cancellationToken);
        var read = Math.Min(buffer.Length, prefix.Length - position);
        prefix.Span.Slice(position, read).CopyTo(buffer.Span);
        position += read;
        return ValueTask.FromResult(read);
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
