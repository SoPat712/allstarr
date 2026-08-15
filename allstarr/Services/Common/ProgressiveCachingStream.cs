namespace allstarr.Services.Common;

/// <summary>
/// Relays a sequential upstream audio response immediately while writing the same
/// bytes to a temporary cache artifact. The artifact is published only after EOF.
/// </summary>
public sealed class ProgressiveCachingStream : Stream
{
    private readonly Stream source;
    private readonly Stream cache;
    private readonly Func<Task> complete;
    private readonly Action abort;
    private bool completed;
    private bool disposed;

    public ProgressiveCachingStream(
        Stream source,
        Stream cache,
        string contentType,
        Func<Task> complete,
        Action abort)
    {
        this.source = source;
        this.cache = cache;
        ContentType = contentType;
        this.complete = complete;
        this.abort = abort;
    }

    public string ContentType { get; }
    public override bool CanRead => !disposed && source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read > 0)
        {
            cache.Write(buffer, offset, read);
        }
        else
        {
            CompleteAsync().GetAwaiter().GetResult();
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken);
        if (read > 0)
        {
            // Cache completion belongs to the server, not to the client request.
            await cache.WriteAsync(buffer[..read], CancellationToken.None);
        }
        else
        {
            await CompleteAsync();
        }

        return read;
    }

    private async Task CompleteAsync()
    {
        if (completed) return;
        completed = true;
        await cache.FlushAsync(CancellationToken.None);
        await cache.DisposeAsync();
        await source.DisposeAsync();
        await complete();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;
        disposed = true;
        if (disposing && !completed)
        {
            source.Dispose();
            cache.Dispose();
            abort();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (!completed)
        {
            await source.DisposeAsync();
            await cache.DisposeAsync();
            abort();
        }

        GC.SuppressFinalize(this);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
