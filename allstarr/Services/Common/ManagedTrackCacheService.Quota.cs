using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;

namespace allstarr.Services.Common;

public sealed partial class ManagedTrackCacheService
{
    private readonly SemaphoreSlim audioBudgetGate = new(1, 1);
    private readonly object audioAllocationGate = new();
    private readonly List<AudioAllocation> audioAllocations = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AudioCacheIdentity> publishedIdentities = new(StringComparer.Ordinal);
    private readonly IDownloadedSongMappingStore? cacheMappings;
    private readonly IManagedFileOperations fileOperations;
    private readonly MediaCapacityLimits mediaLimits;
    private readonly Func<long>? audioMaximumOverride;
    private ManagedFileSystemIdentity? originalRootIdentity;
    private Microsoft.Win32.SafeHandles.SafeFileHandle? originalRootHandle;
    internal string OriginalAudioRoot => Path.Combine(Path.GetTempPath(), "allstarr-original-" +
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AudioCacheRoot))));
    private long AudioMaximum => audioMaximumOverride?.Invoke() ?? mediaLimits.AudioMaximumBytes;
    private string AudioCacheRoot => Path.GetFullPath(Path.Combine(configuration["Library:DownloadPath"] ?? "./downloads", "cache"));

    private void ValidateOriginalRoot(bool create)
    {
        var root = OriginalAudioRoot;
        try
        {
            if (!NoLinkedAncestors(root)) throw new LocalCapacityException("audio-cache-root");
            if (create && !Directory.Exists(root))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
                else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            if (!Directory.Exists(root)) return;
            if (!NoLinkedAncestors(root)) throw new LocalCapacityException("audio-cache-root");
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(root) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                 UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new LocalCapacityException("audio-cache-root");
            if (!fileOperations.TryOpenDirectory(root, out var handle, out var actual))
            { handle.Dispose(); throw new LocalCapacityException("audio-cache-root"); }
            try
            {
                lock (audioAllocationGate)
                {
                    if (originalRootIdentity != null && !Same(originalRootIdentity, actual)) throw new LocalCapacityException("audio-cache-root");
                    if (originalRootHandle == null)
                    {
                        originalRootIdentity = actual;
                        originalRootHandle = handle;
                        handle = null!;
                    }
                }
            }
            finally { handle?.Dispose(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new LocalCapacityException("audio-cache-root"); }
    }

    internal Task TransferCompletionAsync(allstarr.Core.Capabilities.ProviderExternalResourceId track,
        DownloadedSongMappingScope scope, long? revision, bool originalDownload = false) =>
        active.TryGetValue(Key(scope, revision, track.ProviderId, track.Value, originalDownload), out var transfer)
            ? transfer.Work : Task.CompletedTask;

    private sealed record AudioCacheIdentity(string Device, string File, long Length, string Sha256, DateTimeOffset LastUsed,
        string Representation = "Playback", long? SourceAccountRevision = null, double? DurationSeconds = null);
    private sealed class AudioAllocation(string path, FileStream? producer, long reserved, long admissionLimit)
    {
        public string Path = path;
        public FileStream? Producer = producer;
        public FileStream? Anchor;
        public string? ContentHash;
        public double? VerifiedDuration;
        public ManagedFileSystemIdentity? Identity;
        public long Reserved = reserved;
        public long AdmissionLimit = admissionLimit;
        public int Readers;
        public bool Producing = producer != null;
    }

    private async Task<AudioAllocation> ReserveAudioAsync(string path, FileStream producer, long? length, CancellationToken token)
    {
        await audioBudgetGate.WaitAsync(token);
        try
        {
            var limit = AudioMaximum;
            var bytes = length is > 0 ? length.Value : Math.Min(limit, 1024 * 1024);
            await MakeAudioSpaceAsync(bytes, limit, token);
            var entry = new AudioAllocation(path, producer, bytes, limit)
            {
                Identity = Identity(producer),
                Anchor = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            };
            lock (audioAllocationGate) audioAllocations.Add(entry);
            return entry;
        }
        finally { audioBudgetGate.Release(); }
    }

    private async Task GrowAudioAsync(AudioAllocation entry, long needed, CancellationToken token)
    {
        if (needed <= entry.Reserved) return;
        await audioBudgetGate.WaitAsync(token);
        try
        {
            var desired = Math.Min(entry.AdmissionLimit, Math.Max(needed, Math.Max(1024 * 1024, entry.Reserved * 2)));
            if (desired < needed) throw new LocalCapacityException("audio-cache");
            await MakeAudioSpaceAsync(desired - entry.Reserved, entry.AdmissionLimit, token);
            lock (audioAllocationGate) entry.Reserved = desired;
        }
        finally { audioBudgetGate.Release(); }
    }

    private void AudioPathChanged(AudioAllocation entry, string path)
    {
        lock (audioAllocationGate) { entry.Path = path; entry.Identity = entry.Anchor is { } anchor ? Identity(anchor) : Identity(path); }
    }

    private void AudioCompleted(AudioAllocation entry)
    {
        lock (audioAllocationGate)
        {
            entry.Anchor?.Dispose();
            entry.Anchor = null;
            entry.Producing = false;
            entry.Producer = null;
            if (entry.Readers == 0) audioAllocations.Remove(entry);
        }
    }

    private void AudioPin(AudioAllocation entry)
    {
        lock (audioAllocationGate) entry.Readers++;
    }

    private void AudioClosed(AudioAllocation entry)
    {
        lock (audioAllocationGate)
        {
            entry.Readers--;
            if (entry.Readers == 0 && !entry.Producing) audioAllocations.Remove(entry);
        }
    }

    private async Task<Stream> OpenPinnedAudioAsync(string path, CancellationToken token)
    {
        if (!IsOwnedCachePath(path)) return File.OpenRead(path);
        await audioBudgetGate.WaitAsync(token);
        FileStream? file = null;
        try
        {
            file = File.OpenRead(path);
            var identity = Identity(file);
            AudioAllocation entry;
            lock (audioAllocationGate)
            {
                entry = audioAllocations.FirstOrDefault(item => item.Path == path && Same(item.Identity, identity))
                    ?? new AudioAllocation(path, null, file.Length, AudioMaximum) { Identity = identity };
                if (!audioAllocations.Contains(entry)) audioAllocations.Add(entry);
                entry.Readers++;
            }
            try { await TouchAudioAsync(path, identity, token); }
            catch { AudioClosed(entry); throw; }
            var result = new PinnedAudioStream(file, () => AudioClosed(entry));
            file = null;
            return result;
        }
        finally { file?.Dispose(); audioBudgetGate.Release(); }
    }

    private async Task TouchAudioAsync(string path, ManagedFileSystemIdentity? identity, CancellationToken token)
    {
        if (cacheMappings == null)
        {
            if (publishedIdentities.TryGetValue(path, out var known) && Matches(known, identity))
                publishedIdentities[path] = known with { LastUsed = clock.GetUtcNow() };
            return;
        }
        var rows = (await cacheMappings.ListCacheMappingsAsync(token)).Where(item => item.LocalPath == path).ToArray();
        if (rows.Length != 1 || ReadIdentity(rows[0].CacheIdentityJson) is not { } saved || !Matches(saved, identity)) return;
        var updated = saved with { LastUsed = clock.GetUtcNow() };
        if (await cacheMappings.TouchCacheAsync(rows[0], JsonSerializer.Serialize(updated), token))
            publishedIdentities[path] = updated;
    }

    private async Task MarkAudioPublishedAsync(AudioAllocation entry, DownloadedSongMappingScope scope,
        string provider, string track, CancellationToken token)
    {
        await using var stream = File.OpenRead(entry.Path);
        var identity = Identity(stream);
        if (identity == null || identity.LinkCount != 1 || !SafeCachePath(entry.Path) ||
            entry.Anchor == null || !Same(identity, Identity(entry.Anchor))) return;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        if (hash != entry.ContentHash || !Same(identity, Identity(entry.Path)) || !Same(identity, Identity(entry.Anchor))) return;
        var stamp = new AudioCacheIdentity(identity.DeviceId, identity.FileId, stream.Length, hash, clock.GetUtcNow(),
            scope.Representation.ToString(), scope.SourceAccountRevision, entry.VerifiedDuration);
        if (cacheMappings != null && await cacheMappings.MarkCacheOwnedAsync(scope, provider, track, entry.Path,
                JsonSerializer.Serialize(stamp), token) == null) return;
        lock (audioAllocationGate) publishedIdentities[entry.Path] = stamp;
    }

    public async Task CleanupExpiredAudioAsync(TimeSpan retention, CancellationToken token = default)
    {
        if (retention <= TimeSpan.Zero) return;
        await audioBudgetGate.WaitAsync(token);
        try { await MakeAudioSpaceAsync(0, long.MaxValue, token, clock.GetUtcNow() - retention); }
        finally { audioBudgetGate.Release(); }
    }

    private async Task MakeAudioSpaceAsync(long requested, long maximum, CancellationToken token, DateTimeOffset? expiredBefore = null)
    {
        if (requested > maximum) throw new LocalCapacityException("audio-cache");
        IReadOnlyList<DownloadedSongMappingEntity> rows = cacheMappings == null ? [] : await cacheMappings.ListCacheMappingsAsync(token);
        string[] entries;
        try
        {
            ValidateOriginalRoot(false);
            entries = CacheFiles(AudioCacheRoot).Concat(CacheFiles(OriginalAudioRoot)).ToArray();
            ValidateOriginalRoot(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new LocalCapacityException("audio-cache"); }
        var candidates = new List<(string Path, AudioCacheIdentity Identity, DownloadedSongMappingEntity? Mapping)>();
        long used;
        AudioAllocation[] allocations;
        lock (audioAllocationGate) { allocations = audioAllocations.ToArray(); used = allocations.Sum(item => item.Reserved); }
        foreach (var path in entries)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var identity = Identity(path);
            if (allocations.Any(item => item.Path == path && Same(CurrentIdentity(item), identity))) continue;
            used = checked(used + info.Length);
            var mappings = rows.Where(item => item.LocalPath == path).ToArray();
            var stamp = cacheMappings == null ? publishedIdentities.GetValueOrDefault(path)
                : mappings.Length == 1 ? ReadIdentity(mappings[0].CacheIdentityJson) : null;
            if (stamp != null && stamp.Length == info.Length && Matches(stamp, identity) && identity?.LinkCount == 1 &&
                SafeCachePath(path) && !allocations.Any(item => item.Path == path))
                candidates.Add((path, stamp, mappings.SingleOrDefault()));
        }
        foreach (var candidate in candidates.OrderBy(item => item.Identity.LastUsed).ThenBy(item => item.Path, StringComparer.Ordinal))
        {
            if (expiredBefore is { } cutoff ? candidate.Identity.LastUsed >= cutoff : used <= maximum - requested) break;
            bool Remove()
            {
                var current = Identity(candidate.Path);
                if (!SafeCachePath(candidate.Path) || current?.LinkCount != 1 || !Matches(candidate.Identity, current)) return false;
                File.Delete(candidate.Path);
                return true;
            }
            try
            {
                await using var checking = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                if (!Matches(candidate.Identity, Identity(checking)) || checking.Length != candidate.Identity.Length ||
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(checking, token)) != candidate.Identity.Sha256) continue;
                checking.Close();
                var removed = candidate.Mapping != null && cacheMappings != null
                    ? await cacheMappings.RemoveCacheFileAsync(candidate.Mapping, Remove, token) : Remove();
                if (!removed) continue;
                used -= candidate.Identity.Length;
                completedMedia.Remove(candidate.Path);
                publishedIdentities.TryRemove(candidate.Path, out _);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (expiredBefore == null && used > maximum - requested) throw new LocalCapacityException("audio-cache");
    }

    private IEnumerable<string> CacheFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return Path.GetFullPath(path);
            }
        }
    }

    private bool SafeCachePath(string path) => IsOwnedCachePath(path) && NoLinkedAncestors(path);

    private static bool NoLinkedAncestors(string path)
    {
        try
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private ManagedFileSystemIdentity? Identity(Stream stream)
    {
        try { return fileOperations.TryGetFileIdentity(stream, out var value) ? value : null; }
        catch (ObjectDisposedException) { return null; }
    }
    private ManagedFileSystemIdentity? Identity(string path) => fileOperations.TryGetFileIdentity(path, out var value) ? value : null;
    private ManagedFileSystemIdentity? CurrentIdentity(AudioAllocation entry) => entry.Anchor is { } anchor ? Identity(anchor) ?? entry.Identity : entry.Identity;
    private static bool Same(ManagedFileSystemIdentity? one, ManagedFileSystemIdentity? two) => one != null && two != null &&
        one.DeviceId == two.DeviceId && one.FileId == two.FileId;
    private static bool Matches(AudioCacheIdentity saved, ManagedFileSystemIdentity? actual) => actual != null && saved.Device == actual.DeviceId && saved.File == actual.FileId;
    private static AudioCacheIdentity? ReadIdentity(string? json)
    {
        try { return json == null ? null : JsonSerializer.Deserialize<AudioCacheIdentity>(json); }
        catch (JsonException) { return null; }
    }

    internal static DateTimeOffset? CacheLastUsed(string? identityJson) =>
        ReadIdentity(identityJson) is { LastUsed: var lastUsed } && lastUsed != default ? lastUsed : null;

    private sealed class PinnedAudioStream(Stream source, Action closed) : Stream
    {
        private int disposed;
        public Stream Source => source;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => source.ReadAsync(buffer, token);
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            { try { source.Dispose(); } finally { closed(); } }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            { try { await source.DisposeAsync(); } finally { closed(); } }
            GC.SuppressFinalize(this);
        }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
