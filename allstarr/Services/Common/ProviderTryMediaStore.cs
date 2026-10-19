using System.Net;
using System.Security.Cryptography;

namespace allstarr.Services.Common;

public sealed record ProviderTryMediaTicket(string Token, DateTimeOffset ExpiresAt)
{
    public string Url => $"/api/admin/providers/try/media/{Token}";
}

public sealed class ProviderTryMediaStore : IDisposable
{
    public const long DefaultMaximumTemporaryBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderTryMediaEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingCleanup> _pendingDeletion = new(StringComparer.Ordinal);
    private ProviderTryDownloadReservation? _downloadReservation;
    private readonly TimeProvider _clock;
    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly ITimer _timer;
    private long _temporaryBytes;
    private long _sequence;
    private bool _disposed;

    public ProviderTryMediaStore(TimeProvider? clock = null, string? temporaryDirectory = null,
        long maximumTemporaryBytes = DefaultMaximumTemporaryBytes)
    {
        if (maximumTemporaryBytes < 0 || maximumTemporaryBytes > DefaultMaximumTemporaryBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumTemporaryBytes));
        _clock = clock ?? TimeProvider.System;
        _directory = temporaryDirectory ?? Path.Combine(Path.GetTempPath(), "allstarr-provider-try-media");
        _maximumBytes = maximumTemporaryBytes;
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var path in Directory.EnumerateFiles(_directory).Where(path =>
                     Path.GetExtension(path) is ".media" or ".part"))
            File.Delete(path);
        _timer = _clock.CreateTimer(_ => PruneExpired(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public ProviderTryDownloadReservation? TryReserveDownload()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PruneExpiredLocked();
            var available = _maximumBytes - _temporaryBytes;
            if (_downloadReservation != null || available <= 0) return null;
            var reservation = new ProviderTryDownloadReservation(this, available);
            _temporaryBytes += available;
            _downloadReservation = reservation;
            return reservation;
        }
    }

    internal ProviderTryMediaTicket AdoptFile(ProviderTryDownloadReservation reservation, Guid userId,
        string sourcePath, string contentType, string fileName, Action cleanup)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || !ReferenceEquals(_downloadReservation, reservation), reservation);
            if (userId == Guid.Empty) throw new ArgumentException("A user identity is required.", nameof(userId));
            ArgumentNullException.ThrowIfNull(cleanup);
            var path = Path.GetFullPath(sourcePath);
            if (_entries.Values.Any(candidate => candidate.Path == path) || _pendingDeletion.ContainsKey(path))
                throw new IOException("This media file is already registered.");
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("The media file is unavailable.");
            var length = file.Length;
            if (length <= 0 || length > reservation.MaximumBytes)
                throw new IOException("The media file exceeds the reserved storage limit or is empty.");
            var entry = new ProviderTryMediaEntry(userId, NewTicket(), ++_sequence,
                contentType, fileName, length, path, null, cleanup);
            RegisterLocked(entry);
            _temporaryBytes -= reservation.MaximumBytes - length;
            _downloadReservation = null;
            return entry.Ticket;
        }
    }

    internal void ReleaseDownload(ProviderTryDownloadReservation reservation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_downloadReservation, reservation)) return;
            _temporaryBytes -= reservation.MaximumBytes;
            _downloadReservation = null;
        }
    }

    internal void AbandonDownload(ProviderTryDownloadReservation reservation, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (_gate)
        {
            if (!ReferenceEquals(_downloadReservation, reservation)) return;
            var key = "reserved-cleanup-" + Guid.NewGuid().ToString("N");
            _pendingDeletion.Add(key, new(reservation.MaximumBytes, cleanup, CallbackOnly: true));
            _downloadReservation = null;
            if (!TryCleanup(null, cleanup)) return;
            _temporaryBytes -= reservation.MaximumBytes;
            _pendingDeletion.Remove(key);
        }
    }

    public async Task<ProviderTryMediaTicket> CreateFileAsync(Guid userId, string sourcePath, string contentType,
        string fileName, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user identity is required.", nameof(userId));
        var path = Path.Combine(_directory, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + ".part");
        long reserved = 0;
        var registered = false;
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var buffer = new byte[65536];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        PruneExpiredLocked();
                        if (_temporaryBytes > _maximumBytes - read)
                            throw new IOException("Temporary media storage is full.");
                        _temporaryBytes += read;
                        reserved += read;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var finalPath = Path.ChangeExtension(path, ".media");
                File.Move(path, finalPath);
                path = finalPath;
                var entry = new ProviderTryMediaEntry(userId, NewTicket(), ++_sequence,
                    contentType, fileName, reserved, path, null);
                RegisterLocked(entry);
                registered = true;
                return entry.Ticket;
            }
        }
        finally
        {
            if (!registered)
            {
                lock (_gate)
                {
                    if (TryDelete(path)) _temporaryBytes -= reserved;
                    else _pendingDeletion[path] = new(reserved, () => File.Delete(path));
                }
            }
        }
    }

    public ProviderTryMediaTicket CreateStream(Guid userId,
        Func<string?, CancellationToken, Task<HttpResponseMessage>> openResponse,
        string contentType, string fileName, long? length = null)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user identity is required.", nameof(userId));
        ArgumentNullException.ThrowIfNull(openResponse);
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = new ProviderTryMediaEntry(userId, NewTicket(), ++_sequence,
                contentType, fileName, length, null, openResponse);
            RegisterLocked(entry);
            return entry.Ticket;
        }
    }

    internal ProviderTryMediaEntry? Find(Guid userId, string token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PruneExpiredLocked();
            return _entries.TryGetValue(token, out var entry) && entry.UserId == userId ? entry : null;
        }
    }

    public void PruneExpired()
    {
        lock (_gate)
        {
            if (!_disposed) PruneExpiredLocked();
        }
    }

    private ProviderTryMediaTicket NewTicket() => new(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
        _clock.GetUtcNow() + TicketLifetime);

    private void RegisterLocked(ProviderTryMediaEntry entry)
    {
        PruneExpiredLocked();
        foreach (var oldest in _entries.Values.Where(item => item.UserId == entry.UserId)
                     .OrderBy(item => item.Sequence).Take(Math.Max(0, _entries.Values.Count(item => item.UserId == entry.UserId) - 3))
                     .ToArray())
            RemoveLocked(oldest);
        _entries.Add(entry.Ticket.Token, entry);
    }

    private void PruneExpiredLocked()
    {
        foreach (var pending in _pendingDeletion.ToArray())
        {
            if (!TryCleanup(pending.Value.CallbackOnly ? null : pending.Key, pending.Value.Cleanup)) continue;
            _temporaryBytes -= pending.Value.Length;
            _pendingDeletion.Remove(pending.Key);
        }
        var now = _clock.GetUtcNow();
        foreach (var entry in _entries.Values.Where(item => item.Ticket.ExpiresAt <= now).ToArray())
            RemoveLocked(entry);
    }

    private void RemoveLocked(ProviderTryMediaEntry entry)
    {
        if (entry.Path != null)
        {
            var cleanup = entry.Cleanup ?? (() => File.Delete(entry.Path));
            if (TryCleanup(entry.Path, cleanup)) _temporaryBytes -= entry.Length ?? 0;
            else _pendingDeletion[entry.Path] = new(entry.Length ?? 0, cleanup);
        }
        _entries.Remove(entry.Ticket.Token);
    }

    private static bool TryDelete(string path) => TryCleanup(path, () => File.Delete(path));

    private static bool TryCleanup(string? path, Action cleanup)
    {
        try
        {
            cleanup();
            return path == null || !File.Exists(path);
        }
        catch (Exception) { return false; }
    }

    private sealed record PendingCleanup(long Length, Action Cleanup, bool CallbackOnly = false);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            if (_downloadReservation is { } reservation) ReleaseDownload(reservation);
            foreach (var entry in _entries.Values.ToArray()) RemoveLocked(entry);
            foreach (var pending in _pendingDeletion) TryCleanup(pending.Value.CallbackOnly ? null : pending.Key, pending.Value.Cleanup);
        }
    }
}

public sealed class ProviderTryDownloadReservation : IDisposable
{
    private readonly ProviderTryMediaStore _store;

    internal ProviderTryDownloadReservation(ProviderTryMediaStore store, long maximumBytes)
    {
        _store = store;
        MaximumBytes = maximumBytes;
    }

    public long MaximumBytes { get; }

    public ProviderTryMediaTicket CreateFileTicket(Guid userId, string sourcePath, string contentType,
        string fileName, Action cleanup) => _store.AdoptFile(this, userId, sourcePath, contentType, fileName, cleanup);

    public void Abandon(Action cleanup) => _store.AbandonDownload(this, cleanup);

    public void Dispose() => _store.ReleaseDownload(this);
}

internal sealed record ProviderTryMediaEntry(Guid UserId, ProviderTryMediaTicket Ticket, long Sequence,
    string ContentType, string FileName, long? Length, string? Path,
    Func<string?, CancellationToken, Task<HttpResponseMessage>>? OpenResponse, Action? Cleanup = null)
{
    public Task<HttpResponseMessage> OpenAsync(string? range, CancellationToken cancellationToken)
    {
        if (OpenResponse != null) return OpenResponse(range, cancellationToken);
        var stream = new FileStream(Path!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }
}
