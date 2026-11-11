namespace allstarr.Services.Common;

public sealed partial class FileMediaApplicationCache
{
    private sealed record PendingWrite(string Key, string? Value, DateTimeOffset WrittenAt, DateTimeOffset ExpiresAt, Guid? ListenerId, int Bytes,
        MediaEntryMetadata? Access = null)
    {
        public DateTimeOffset LastAccessAt { get; set; } = WrittenAt;
    }

    private readonly object _pendingLock = new();
    private readonly Queue<PendingWrite> _pending = new();
    private readonly Dictionary<string, PendingWrite> _pendingValues = new(StringComparer.Ordinal);
    private Task _writer = Task.CompletedTask;
    private long _pendingBytes;
    private int _pendingCount;
    private int _activeReaders;
    private bool _writing;
    private bool _disposed;

    internal bool QueueWrite(string key, string? value, TimeSpan? expiry = null, Guid? listenerId = null)
    {
        if (!CanStore(key)) return false;
        var bytes = value == null ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);
        if (bytes > _options.MaximumEntryBytes) return false;
        var writtenAt = _clock.UtcNow;
        DateTimeOffset expiresAt;
        try { expiresAt = writtenAt.Add(expiry ?? ApplicationCachePolicyRegistry.Resolve(key).FreshFor); }
        catch (ArgumentOutOfRangeException) { return false; }
        return Enqueue(new(key, value, writtenAt, expiresAt, listenerId, bytes));
    }

    private bool Enqueue(PendingWrite write)
    {
        lock (_pendingLock)
        {
            // Cache persistence is best effort; slow storage must not grow an unbounded backlog.
            if (_disposed || _pendingCount >= 128 || _pendingBytes + write.Bytes > MemoryApplicationCache.MaximumBytes)
                return false;
            _pending.Enqueue(write);
            if (write.Access == null) _pendingValues[write.Key] = write;
            _pendingCount++;
            _pendingBytes += write.Bytes;
            if (!_writing)
            {
                _writing = true;
                _writer = Task.Run(WritePendingAsync);
            }
            return true;
        }
    }

    internal Task FlushWritesAsync()
    {
        lock (_pendingLock) return _writer;
    }

    private async Task WritePendingAsync()
    {
        while (true)
        {
            PendingWrite write;
            lock (_pendingLock)
            {
                if (!_pending.TryDequeue(out write!))
                {
                    _writing = false;
                    return;
                }
            }
            try
            {
                // Pace background disk work so foreground reads have room to finish.
                await Task.Delay(25);
                if (write.Access is { } access)
                {
                    await WaitForWriteAsync();
                    try
                    {
                        var path = PathsFor(write.Key).Metadata;
                        var current = await ReadMetadataAsync(path);
                        if (current != null && current.WrittenAt == access.WrittenAt && current.PayloadHash == access.PayloadHash)
                            await WriteMetadataAsync(path, current with { LastAccessAt = access.LastAccessAt });
                    }
                    finally { _gate.Release(); }
                }
                else if (write.Value == null || !await PersistEntryAsync(write))
                    await DeleteEntryAsync(write.Key);
            }
            catch (Exception error)
            {
                _logger.LogDebug("Disk cache persistence skipped ({ErrorType})", error.GetType().Name);
            }
            finally
            {
                lock (_pendingLock)
                {
                    if (ReferenceEquals(_pendingValues.GetValueOrDefault(write.Key), write)) _pendingValues.Remove(write.Key);
                    _pendingCount--;
                    _pendingBytes -= write.Bytes;
                }
            }
        }
    }

    private async Task WaitForWriteAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_pendingLock)
            {
                if (_activeReaders == 0 && _gate.Wait(0, cancellationToken)) return;
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    private bool CanStore(string key) => IsEnabled && ApplicationCachePayloadPolicy.IsValidKey(key) &&
        ApplicationCachePolicyRegistry.TryClassify(key, out _) &&
        (!ApplicationCachePayloadPolicy.IsMemoryEligible(key) || ApplicationCachePayloadPolicy.IsRestartSafe(key)) &&
        ApplicationCachePolicyRegistry.IsEnabled(key, _settings);
}
