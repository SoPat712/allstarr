using allstarr.Core.ManagedFiles;

namespace allstarr.Tests;

internal sealed class PausingManagedFileOperations : IManagedFileOperations
{
    private readonly PhysicalManagedFileOperations inner = new();
    private string? pausedPath;
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Arm(string path) => Volatile.Write(ref pausedPath, path);

    public Stream OpenRead(string path, bool sequential = false)
    {
        var opened = ((IManagedFileOperations)inner).OpenRead(path, sequential);
        return sequential && path == Interlocked.CompareExchange(ref pausedPath, null, path)
            ? new PausingStream(opened, this) : opened;
    }
    public bool TryGetFileIdentity(string path, out ManagedFileSystemIdentity identity) => inner.TryGetFileIdentity(path, out identity);
    public bool TryGetFileIdentity(Stream stream, out ManagedFileSystemIdentity identity) => inner.TryGetFileIdentity(stream is PausingStream pause ? pause.Inner : stream, out identity);
    public bool TryCreateHardLink(string linkPath, string existingPath) => inner.TryCreateHardLink(linkPath, existingPath);
    public bool TryCreateReflink(string destinationPath, string sourcePath) => inner.TryCreateReflink(destinationPath, sourcePath);
    public Task CopyAsync(string sourcePath, string destinationPath, CancellationToken token) => inner.CopyAsync(sourcePath, destinationPath, token);
    public void MoveNoReplace(string sourcePath, string destinationPath) => inner.MoveNoReplace(sourcePath, destinationPath);

    private sealed class PausingStream(Stream inner, PausingManagedFileOperations owner) : Stream
    {
        private int paused;
        internal Stream Inner => inner;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref paused, 1) == 0)
            {
                owner.Entered.TrySetResult();
                await owner.Resume.Task.WaitAsync(token);
            }
            return await inner.ReadAsync(buffer, token);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
