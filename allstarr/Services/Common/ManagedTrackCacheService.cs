using System.Collections.Concurrent;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Local;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

public sealed class ManagedTrackCacheService : IAsyncDisposable
{
    private readonly IConfiguration configuration;
    private readonly IOptions<SubsonicSettings> settings;
    private readonly ILocalLibraryService localLibrary;
    private readonly ILogger<ManagedTrackCacheService> logger;
    private readonly ConcurrentDictionary<string, Transfer> active = new(StringComparer.Ordinal);
    private readonly object lifecycle = new();
    private readonly CancellationTokenSource shutdown;
    private bool disposed;
    private Task? disposal;

    public ManagedTrackCacheService(IConfiguration configuration, IOptions<SubsonicSettings> settings,
        ILocalLibraryService localLibrary, ILogger<ManagedTrackCacheService> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        this.configuration = configuration;
        this.settings = settings;
        this.localLibrary = localLibrary;
        this.logger = logger;
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(lifetime?.ApplicationStopping ?? CancellationToken.None);
    }

    public async Task<ProtocolProviderStream?> TryOpenAsync(ProviderExternalResourceId track,
        DownloadedSongMappingScope scope, CancellationToken cancellationToken, long? accountRevision = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (active.TryGetValue(Key(scope, accountRevision, track.ProviderId, track.Value), out var transfer))
        {
            var reader = transfer.TryOpen();
            if (reader != null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(reader) };
                response.Content.Headers.ContentType = new(transfer.Media.MimeType);
                return new(response, CacheLease(transfer.Media, reader.CanSeek, progressive: true), track.ProviderId, track.Value,
                    scope.ProviderAccountId, IsCached: true, EffectiveQuality: scope.AudioQuality,
                    SelectionReason: "progressive-cache", ServingAccountRevision: accountRevision);
            }
        }
        var path = await localLibrary.GetLocalPathForExternalSongAsync(scope, track.ProviderId, track.Value);
        if (path == null) return null;
        try
        {
            if (Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase) &&
                IsOwnedCachePath(path) && HasId3Prefix(path))
            {
                File.Move(path, path + $".legacy-id3-{Guid.NewGuid():N}");
                return null;
            }
            var file = File.OpenRead(path);
            if (file.Length == 0) { file.Dispose(); return null; }
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = extension switch
            {
                "flac" => "audio/flac",
                "m4a" or "mp4" => "audio/mp4",
                "aac" => "audio/aac",
                "opus" => "audio/opus",
                "ogg" => "audio/ogg",
                _ => "audio/mpeg"
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(file) };
            response.Content.Headers.ContentType = new(mime);
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new(response, CacheLease(new(mime, extension, extension), true), track.ProviderId, track.Value,
                scope.ProviderAccountId, IsCached: true, EffectiveQuality: scope.AudioQuality,
                ServingAccountRevision: accountRevision);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task WrapAsync(ProtocolProviderStream stream, ProtocolExecutionContext protocol,
        string providerId, string externalId, bool headOnly, Func<Task<Song?>> metadataFactory,
        CancellationToken cancellationToken)
    {
        if (stream.IsCached || !IsCacheMode() || headOnly || !IsCompleteResponse(stream.Response) || protocol.Actor == null) return;
        if (stream.ServingExternalId == null && stream.ServingProviderId != providerId) return;
        providerId = stream.ServingProviderId;
        externalId = stream.ServingExternalId ?? externalId;
        var scope = new DownloadedSongMappingScope(stream.ServingAccountId, stream.EffectiveQuality);
        var key = Key(scope, stream.ServingAccountRevision, providerId, externalId);
        if (active.ContainsKey(key)) return;
        var cacheRoot = Path.Combine(configuration["Library:DownloadPath"] ?? "./downloads", "cache");
        var partial = Path.Combine(cacheRoot, ".incoming", $"provider-{Guid.NewGuid():N}.partial");
        FileStream output;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            output = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not open the managed track cache for {ProviderId}", providerId);
            return;
        }
        var original = stream.Response.Content;
        Stream source;
        try { source = await original.ReadAsStreamAsync(cancellationToken); }
        catch { await output.DisposeAsync(); TryDelete(partial); throw; }
        Transfer? transfer = null;
        lock (lifecycle)
        {
            if (!disposed && !shutdown.IsCancellationRequested && !active.ContainsKey(key))
            {
                transfer = new(this, key, scope, providerId, externalId, stream.Lease.Media, cacheRoot,
                    partial, output, original, source,
                    stream.Response.StatusCode == HttpStatusCode.PartialContent ? original.Headers.ContentRange?.Length : original.Headers.ContentLength,
                    metadataFactory, shutdown.Token);
                active[key] = transfer;
                transfer.StartWorker();
            }
        }
        if (transfer == null)
        {
            await output.DisposeAsync();
            TryDelete(partial);
            return;
        }
        var reader = transfer.TryOpen();
        if (reader == null)
        {
            transfer.Stop();
            throw new IOException("The progressive media cache could not open a reader.");
        }
        var replacement = new ProgressiveContent(reader);
        foreach (var header in original.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        stream.Response.Content = replacement;
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal != null) return new(disposal);
            disposed = true;
            disposal = StopAsync(active.Values.ToArray());
            return new(disposal);
        }
    }

    private async Task StopAsync(Transfer[] transfers)
    {
        await shutdown.CancelAsync();
        await Task.WhenAll(transfers.Select(transfer => transfer.Work));
        shutdown.Dispose();
    }

    private static string Key(DownloadedSongMappingScope scope, long? revision, string provider, string track) =>
        $"{scope.Key}\n{revision}\n{provider}\n{track}";

    internal static string? SeekRung(HttpContent content) => content is ProgressiveContent progressive
        ? progressive.Reader.CanSeek ? "progressive-cache" : "sequential-cache" : null;

    private sealed class ProgressiveContent(Stream reader) : StreamContent(reader)
    {
        public Stream Reader { get; } = reader;
    }

    private static ProviderStreamLease CacheLease(ProviderMediaFormat media, bool ranges, bool progressive = false) => new(
        progressive ? "managed-progressive-cache" : "managed-cache",
        new Uri("https://allstarr.invalid/managed-cache"), DateTimeOffset.UtcNow.AddHours(1), ranges, ranges, media,
        ProviderStreamRetryBehavior.DoNotRetry);

    private sealed class Transfer
    {
        private readonly ManagedTrackCacheService owner;
        private readonly string key;
        private readonly DownloadedSongMappingScope scope;
        private readonly string provider;
        private readonly string track;
        private readonly string cacheRoot;
        private readonly FileStream output;
        private readonly HttpContent original;
        private readonly Stream source;
        private readonly Func<Task<Song?>> metadata;
        private readonly CancellationTokenSource cancel;
        private readonly TaskCompletionSource ready = Signal();
        private readonly TaskCompletionSource complete = Signal();
        private readonly object gate = new();
        private TaskCompletionSource changed = Signal();
        private string path;
        private long available;
        private long? total;
        private int readers;
        private bool finished;
        private bool failed;
        private bool retired;
        public ProviderMediaFormat Media { get; }
        public Task Work { get; private set; } = Task.CompletedTask;

        public Transfer(ManagedTrackCacheService owner, string key, DownloadedSongMappingScope scope,
            string provider, string track, ProviderMediaFormat media, string cacheRoot, string path, FileStream output,
            HttpContent original, Stream source, long? total, Func<Task<Song?>> metadata, CancellationToken stopping)
        {
            this.owner = owner; this.key = key; this.scope = scope; this.provider = provider; this.track = track;
            Media = media; this.cacheRoot = cacheRoot; this.path = path; this.output = output;
            this.original = original; this.source = source; this.total = total; this.metadata = metadata;
            cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        }

        public void StartWorker() => Work = Task.Run(RunAsync);
        public void Stop() => cancel.Cancel();

        public Stream? TryOpen()
        {
            lock (gate)
            {
                if (retired || failed || cancel.IsCancellationRequested) return null;
                FileStream file;
                try { file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous); }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                readers++;
                cancel.CancelAfter(Timeout.InfiniteTimeSpan);
                return new ProgressiveCachingStream(file, Snapshot, Closed,
                    TimeSpan.FromSeconds(3), () => ready.TrySetResult(), TimeSpan.FromSeconds(30));
            }
        }

        private ProgressiveCacheReadState Snapshot()
        {
            lock (gate) return new(available, total, finished, failed, changed.Task, complete.Task);
        }

        private void Advance(bool end = false, bool failure = false)
        {
            TaskCompletionSource signal;
            lock (gate)
            {
                finished = end;
                failed = failure;
                if (end && !failure) total ??= available;
                signal = changed;
                changed = Signal();
            }
            signal.TrySetResult();
        }

        private void Closed()
        {
            lock (gate)
            {
                if (--readers == 0 && !finished)
                    cancel.CancelAfter(TimeSpan.FromSeconds(10));
            }
        }

        private async Task RunAsync()
        {
            var published = false;
            var verified = false;
            using var abort = cancel.Token.Register(original.Dispose);
            try
            {
                await ready.Task.WaitAsync(cancel.Token);
                Task<Song?> metadataTask;
                try { metadataTask = metadata(); }
                catch { metadataTask = Task.FromResult<Song?>(null); }
                // Observe a metadata failure even when the audio source fails first.
                _ = metadataTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancel.Token);
                    lock (gate) total ??= StreamDurationGuidance.DeclaredLength(original);
                    if (read == 0) break;
                    if (total is { } expected && read > expected - available)
                        throw new IOException("The media source exceeded its declared length.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel.Token);
                    await output.FlushAsync(cancel.Token);
                    lock (gate) available = checked(available + read);
                    Advance();
                }
                if (available == 0 || total is { } length && available != length)
                    throw new IOException("The media source ended before its declared length.");
                await output.DisposeAsync();
                original.Dispose();
                verified = true;
                Advance(end: true);
                published = await PublishAsync(metadataTask);
            }
            catch (Exception)
            {
                if (!verified) Advance(end: true, failure: true);
                if (!cancel.IsCancellationRequested)
                    owner.logger.LogWarning("The managed media transfer could not complete for {ProviderId}", provider);
            }
            finally
            {
                try { await output.DisposeAsync(); } catch (IOException) { }
                try { original.Dispose(); } catch (IOException) { }
                lock (gate) retired = true;
                if (!published) TryDelete(path);
                complete.TrySetResult();
                owner.active.TryRemove(key, out _);
                cancel.Dispose();
            }
        }

        private async Task<bool> PublishAsync(Task<Song?> metadataTask)
        {
            var existing = await owner.localLibrary.GetLocalPathForExternalSongAsync(scope, provider, track);
            if (existing != null && File.Exists(existing)) return false;
            Song? song = null;
            try { song = await metadataTask.WaitAsync(cancel.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception) { }
            song ??= new Song { Id = $"ext-{provider}-song-{track}", Title = track, Artist = "Unknown artist", Album = "Unknown album" };
            song.ExternalProvider = provider;
            song.ExternalId = track;
            var final = PathHelper.BuildTrackPath(cacheRoot, song.AlbumArtist ?? song.Artist, song.Album,
                song.Title, song.Track, Extension(Media), provider, track);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            lock (owner.lifecycle)
            {
                cancel.Token.ThrowIfCancellationRequested();
                final = PathHelper.ResolveUniquePath(final);
                lock (gate) { File.Move(path, final); path = final; }
            }
            song.LocalPath = final;
            await owner.localLibrary.RegisterDownloadedSongAsync(scope, song, final);
            owner.logger.LogInformation("Published completed provider stream to the managed track cache for {ProviderId}", provider);
            return true;
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private bool IsCacheMode() => settings.Value.StorageMode == StorageMode.Cache;

    private bool IsOwnedCachePath(string path)
    {
        var cacheRoot = Path.GetFullPath(Path.Combine(
            configuration["Library:DownloadPath"] ?? "./downloads", "cache"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(path).StartsWith(cacheRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static bool HasId3Prefix(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> marker = stackalloc byte[3];
        return file.Read(marker) == marker.Length && marker.SequenceEqual("ID3"u8);
    }

    private static bool IsCompleteResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK) return response.Content.Headers.ContentLength != 0;
        var range = response.Content.Headers.ContentRange;
        return response.StatusCode == HttpStatusCode.PartialContent &&
               range?.From == 0 &&
               range.To.HasValue &&
               range.Length.HasValue &&
               range.To.Value + 1 == range.Length.Value;
    }

    private static string Extension(ProviderMediaFormat media) =>
        (media.Container.Length > 0 ? media.Container : media.Codec).ToLowerInvariant() switch
        {
            "flac" => ".flac",
            "m4a" or "mp4" or "alac" => ".m4a",
            "aac" => ".aac",
            "opus" => ".opus",
            "ogg" or "vorbis" => ".ogg",
            _ => media.MimeType.Contains("flac", StringComparison.OrdinalIgnoreCase) ? ".flac" : ".mp3"
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Only paths created by this cache owner reach this helper.
        }
    }
}
