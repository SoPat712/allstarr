using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using allstarr.Core.ManagedFiles;
using Microsoft.Extensions.Caching.Memory;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Local;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

public sealed partial class ManagedTrackCacheService : IAsyncDisposable
{
    private readonly IConfiguration configuration;
    private readonly IOptions<SubsonicSettings> settings;
    private readonly ILocalLibraryService localLibrary;
    private readonly ILogger<ManagedTrackCacheService> logger;
    private readonly TimeProvider clock;
    private readonly IProviderAudioVerifier mediaVerifier;
    private readonly ConcurrentDictionary<string, Transfer> active = new(StringComparer.Ordinal);
    private readonly MemoryCache completedMedia = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly object lifecycle = new();
    private readonly Dictionary<string, OpenGate> openings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> clientOpens = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown;
    private bool disposed;
    private Task? disposal;

    public ManagedTrackCacheService(IConfiguration configuration, IOptions<SubsonicSettings> settings,
        ILocalLibraryService localLibrary, ILogger<ManagedTrackCacheService> logger,
        IHostApplicationLifetime? lifetime = null, TimeProvider? timeProvider = null,
        IDownloadedSongMappingStore? mappings = null, IManagedFileOperations? files = null,
        MediaCapacityLimits? capacity = null, Func<long>? audioMaximumBytes = null, IProviderAudioVerifier? audioVerifier = null)
    {
        this.configuration = configuration;
        this.settings = settings;
        this.localLibrary = localLibrary;
        this.logger = logger;
        cacheMappings = mappings;
        fileOperations = files ?? new PhysicalManagedFileOperations();
        mediaLimits = capacity ?? new(configuration);
        mediaVerifier = audioVerifier ?? new ProviderAudioVerifier();
        audioMaximumOverride = audioMaximumBytes;
        if (audioMaximumBytes != null && audioMaximumBytes() <= 0) throw new ArgumentOutOfRangeException(nameof(audioMaximumBytes));
        clock = timeProvider ?? TimeProvider.System;
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(lifetime?.ApplicationStopping ?? CancellationToken.None);
    }

    internal async Task<IDisposable?> BeginOpenAsync(ProviderExternalResourceId track,
        DownloadedSongMappingScope scope, long? revision, bool preparation, CancellationToken cancellationToken, bool originalDownload = false)
    {
        var key = Key(scope, revision, track.ProviderId, track.Value, originalDownload);
        var item = $"{track.ProviderId}\n{track.Value}";
        OpenGate opening;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = clock.GetUtcNow();
            foreach (var expired in clientOpens.Where(pair => now - pair.Value >= TimeSpan.FromHours(1)).ToArray())
                clientOpens.Remove(expired.Key);
            if (preparation && (clientOpens.ContainsKey(item) || active.ContainsKey(key))) return null;
            if (!preparation && !originalDownload)
            {
                if (clientOpens.Count >= 4096) clientOpens.Remove(clientOpens.MinBy(pair => pair.Value).Key);
                clientOpens[item] = now;
            }
            if (!openings.TryGetValue(key, out opening!)) openings[key] = opening = new();
            opening.Users++;
        }
        var acquired = false;
        try
        {
            await opening.Semaphore.WaitAsync(cancellationToken);
            acquired = true;
            lock (lifecycle)
            {
                if (preparation && clientOpens.ContainsKey(item))
                {
                    ReleaseOpen(key, opening, true);
                    return null;
                }
            }
            return new OpenHandle(() => ReleaseOpen(key, opening, true));
        }
        catch { ReleaseOpen(key, opening, acquired); throw; }
    }

    private void ReleaseOpen(string key, OpenGate opening, bool acquired)
    {
        lock (lifecycle)
        {
            if (acquired) opening.Semaphore.Release();
            if (--opening.Users == 0)
            {
                openings.Remove(key);
                opening.Semaphore.Dispose();
            }
        }
    }

    private sealed class OpenGate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private sealed class OpenHandle(Action release) : IDisposable
    {
        private Action? remaining = release;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }

    internal async Task<ProviderMediaFormat?> ReadCachedMediaAsync(ProviderExternalResourceId track,
        DownloadedSongMappingScope scope, CancellationToken cancellationToken, long? accountRevision)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope.Representation == DownloadedSongRepresentation.Original && cacheMappings == null) return null;
        if (active.TryGetValue(Key(scope, accountRevision, track.ProviderId, track.Value), out var transfer))
            return transfer.Media;
        var path = await localLibrary.GetLocalPathForExternalSongAsync(scope, track.ProviderId, track.Value);
        if (path == null || !File.Exists(path)) return null;
        if (completedMedia.Get<TrackMediaFacts>(path) is { } retained) return retained.Media;
        try
        {
            await using var file = File.OpenRead(path);
            if (file.Length == 0) return null;
            var bytes = new byte[4096];
            var count = await file.ReadAsync(bytes, cancellationToken);
            return AudioMediaFacts.ReadPrefix(bytes.AsSpan(0, count), file.Length)?.Media;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<ProtocolProviderStream?> TryOpenAsync(ProviderExternalResourceId track,
        DownloadedSongMappingScope scope, CancellationToken cancellationToken, long? accountRevision = null, bool preparation = false,
        long? requestedOffset = null, bool originalDownload = false, bool retainOriginalMedia = false, bool completeOnly = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retainedOriginal = originalDownload && retainOriginalMedia && IsCacheMode();
        if (originalDownload)
            scope = retainedOriginal
                ? new(scope.ProviderAccountId, scope.AudioQuality, DownloadedSongRepresentation.Original, accountRevision)
                : new(scope.ProviderAccountId, scope.AudioQuality);
        if (!completeOnly && active.TryGetValue(Key(scope, accountRevision, track.ProviderId, track.Value, originalDownload), out var transfer))
        {
            if (requestedOffset is > 0 && transfer.CanFetchRange(requestedOffset.Value)) return null;
            var reader = transfer.TryOpen(!preparation);
            if (reader != null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ProgressiveContent(reader, transfer.PlaybackStarted, transfer.PrepareSourceAsync) };
                response.Content.Headers.ContentType = new(transfer.Media.MimeType);
                return new(response, CacheLease(transfer.Media, reader.CanSeek, progressive: true), track.ProviderId, track.Value,
                    scope.ProviderAccountId, IsCached: true, EffectiveQuality: scope.AudioQuality,
                    SelectionReason: "progressive-cache", ServingAccountRevision: accountRevision)
                { FileFacts = transfer.FileFacts };
            }
        }
        if (originalDownload && !retainedOriginal) return null;
        DownloadedSongMappingEntity? originalMapping = null;
        if (retainedOriginal)
        {
            if (cacheMappings == null) return null;
            originalMapping = await cacheMappings.FindAsync(scope, track.ProviderId, track.Value, cancellationToken);
            if (originalMapping == null || !DownloadedSongMappingScope.TryResolve(originalMapping, out var savedScope) ||
                savedScope != scope) return null;
        }
        var path = originalMapping?.LocalPath ?? await localLibrary.GetLocalPathForExternalSongAsync(scope, track.ProviderId, track.Value);
        if (path == null) return null;
        Stream? file = null;
        try
        {
            if (!retainedOriginal && Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase) &&
                IsOwnedCachePath(path) && HasId3Prefix(path))
            {
                File.Move(path, path + $".legacy-id3-{Guid.NewGuid():N}");
                return null;
            }
            file = await OpenPinnedAudioAsync(path, cancellationToken);
            if (file.Length == 0) { file.Dispose(); return null; }
            if (originalMapping != null && !await VerifyOriginalCacheAsync(file, originalMapping, cancellationToken)) return null;
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = extension switch
            {
                "flac" => "audio/flac",
                "m4a" or "mp4" => "audio/mp4",
                "aac" => "audio/aac",
                "opus" => "audio/opus",
                "ogg" => "audio/ogg",
                "wav" => "audio/wav",
                _ => "audio/mpeg"
            };
            var verifiedDuration = publishedIdentities.TryGetValue(path, out var proof) && proof.Length == file.Length ? proof.DurationSeconds : null;
            var facts = completedMedia.Get<TrackMediaFacts>(path) ??
                await ReadCompleteHeaderAsync(file, cancellationToken, verifiedDuration) ??
                await AudioMediaFacts.ReadFileAsync(path, cancellationToken) ?? new(ReadFileMedia(path, mime, extension), file.Length);
            if (verifiedDuration is > 0)
                facts = AudioMediaFacts.WithDecodedDuration(facts, file.Length, verifiedDuration.Value);
            facts = facts with { Tags = facts.Tags ?? AudioMediaFacts.ReadTags(path, facts.Media) };
            var media = facts.Media;
            completedMedia.Set(path, facts, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(file) };
            response.Content.Headers.ContentLength = file.Length;
            file = null;
            response.Content.Headers.ContentType = new(media.MimeType);
            return new(response, CacheLease(media, true), track.ProviderId, track.Value,
                scope.ProviderAccountId, IsCached: true, EffectiveQuality: scope.AudioQuality,
                ServingAccountRevision: accountRevision)
            {
                FileFacts = new()
                {
                    Measured = facts,
                    Media = facts.Media,
                    Size = facts.Size,
                    Catalog = originalMapping == null ? null : new Song { Title = originalMapping.Title, Artist = originalMapping.Artist }
                }
            };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { file?.Dispose(); }
    }

    private static async Task<TrackMediaFacts?> ReadCompleteHeaderAsync(Stream file, CancellationToken token, double? verifiedDuration = null)
    {
        try
        {
            var prefix = new byte[4096];
            var count = await file.ReadAsync(prefix, token);
            var facts = AudioMediaFacts.ReadPrefix(prefix.AsSpan(0, count), file.Length);
            return facts is { Media: { SampleRate: > 0, Channels: > 0 } } && (facts.DurationSeconds is > 0 || verifiedDuration is > 0) &&
                facts.Media.Container is "flac" or "wav" ? facts : null;
        }
        finally { file.Position = 0; }
    }

    public bool CanPrepare(ProtocolExecutionContext context) => IsCacheMode() &&
        context.Actor != null && context.Policy?.AudioQuality != "DataSaver" &&
        context.PreparationQuality != ProviderAudioQuality.DataSaver;

    internal bool CanRetainOriginal(ProtocolExecutionContext context) => IsCacheMode() && context.IsOriginalDownload &&
        context.RetainOriginalMedia && !context.IsReadOnly && !context.IsPreparation;

    internal DownloadedSongMappingScope ScopeFor(ProtocolExecutionContext context, Guid? account,
        ProviderAudioQuality quality, long? revision) => CanRetainOriginal(context)
            ? new(account, quality, DownloadedSongRepresentation.Original, revision) : new(account, quality);

    private async Task<bool> VerifyOriginalCacheAsync(Stream file, DownloadedSongMappingEntity mapping, CancellationToken token)
    {
        var source = file is PinnedAudioStream pinned ? pinned.Source : file;
        var identity = Identity(source);
        var proof = ReadIdentity(mapping.CacheIdentityJson);
        if (!SafeCachePath(mapping.LocalPath) || identity?.LinkCount != 1 || proof == null || proof.Representation != "Original" ||
            proof.Length != file.Length || !Matches(proof, identity) || !Same(identity, Identity(mapping.LocalPath))) return false;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        file.Position = 0;
        return hash == proof.Sha256 && Same(identity, Identity(mapping.LocalPath)) && Same(identity, Identity(source));
    }

    public async Task WrapAsync(ProtocolProviderStream stream, ProtocolExecutionContext protocol,
        string providerId, string externalId, bool headOnly, Func<Task<Song?>> metadataFactory,
        CancellationToken cancellationToken)
    {
        if (protocol.IsReadOnly || protocol.IsPreparation && !CanPrepare(protocol)) return;
        var retainedOriginal = CanRetainOriginal(protocol);
        var retain = (!protocol.IsOriginalDownload || retainedOriginal) && IsCacheMode();
        var completeResponse = IsCompleteResponse(stream.Response);
        var rangeFill = retain && !completeResponse && stream.OpenOriginalResponseAsync != null &&
            stream.Response.StatusCode == HttpStatusCode.PartialContent &&
            StreamByteRanges.HasValidContentRange(stream.Response.Content.Headers.ContentRange, stream.Response.Content.Headers.ContentLength);
        if (stream.IsCached || stream.Response.Content is ProgressiveContent or RetainedRangeContent || headOnly ||
            !completeResponse && !rangeFill || protocol.Actor == null ||
            !retain && stream.Lease.SupportsByteRanges) return;
        if (stream.ServingExternalId == null && stream.ServingProviderId != providerId) return;
        providerId = stream.ServingProviderId;
        externalId = stream.ServingExternalId ?? externalId;
        var scope = ScopeFor(protocol, stream.ServingAccountId, stream.EffectiveQuality, stream.ServingAccountRevision);
        var key = Key(scope, stream.ServingAccountRevision, providerId, externalId, protocol.IsOriginalDownload);
        if (active.TryGetValue(key, out var existing))
        {
            if (rangeFill) await AttachRangeAsync(stream, existing, cancellationToken);
            return;
        }
        var temporaryOriginal = protocol.IsOriginalDownload && !retainedOriginal;
        if (temporaryOriginal) ValidateOriginalRoot(true);
        var cacheRoot = temporaryOriginal ? OriginalAudioRoot
            : Path.Combine(configuration["Library:DownloadPath"] ?? "./downloads", "cache");
        var partial = temporaryOriginal ? Path.Combine(cacheRoot, $"allstarr-original-{Guid.NewGuid():N}.partial")
            : Path.Combine(cacheRoot, ".incoming", $"provider-{Guid.NewGuid():N}.partial");
        FileStream output;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            output = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!protocol.IsPreparation) logger.LogWarning("Could not open the managed track cache for {ProviderId}", providerId);
            return;
        }
        var original = stream.Response.Content;
        var media = original.Headers.ContentType?.MediaType?.Equals("audio/mp4", StringComparison.OrdinalIgnoreCase) == true &&
            !stream.Lease.Media.MimeType.Equals("audio/mp4", StringComparison.OrdinalIgnoreCase)
            ? new ProviderMediaFormat("audio/mp4", "mp4", "unknown") : stream.Lease.Media;
        stream.FileFacts.Media = media;
        Stream? source;
        try { source = rangeFill ? null : await original.ReadAsStreamAsync(cancellationToken); }
        catch { await output.DisposeAsync(); TryDelete(partial); throw; }
        AudioAllocation allocation;
        try
        {
            allocation = await ReserveAudioAsync(partial, output,
                stream.Response.StatusCode == HttpStatusCode.PartialContent ? original.Headers.ContentRange?.Length : original.Headers.ContentLength,
                cancellationToken);
        }
        catch (LocalCapacityException) when (protocol.IsPreparation || rangeFill)
        {
            await output.DisposeAsync();
            TryDelete(partial);
            return;
        }
        catch { await output.DisposeAsync(); TryDelete(partial); throw; }
        Transfer? transfer = null;
        lock (lifecycle)
        {
            if (!disposed && !shutdown.IsCancellationRequested && !active.ContainsKey(key))
            {
                transfer = new(this, key, scope, providerId, externalId, media, cacheRoot,
                    partial, output, rangeFill ? null : original, source,
                    stream.Response.StatusCode == HttpStatusCode.PartialContent ? original.Headers.ContentRange?.Length : original.Headers.ContentLength,
                    metadataFactory, retain, shutdown.Token, stream.FileFacts,
                    protocol.IsPreparation ? cancellationToken : CancellationToken.None, protocol.IsPreparation,
                    protocol.IsPreparation ? allocation.AdmissionLimit : long.MaxValue,
                    stream.Lease.SupportsByteRanges, protocol.IsOriginalDownload, allocation,
                    retainedOriginal, rangeFill ? stream.OpenOriginalResponseAsync : null);
                active[key] = transfer;
                transfer.StartWorker();
            }
        }
        if (transfer == null)
        {
            await output.DisposeAsync();
            TryDelete(partial);
            AudioCompleted(allocation);
            if (rangeFill && active.TryGetValue(key, out existing)) await AttachRangeAsync(stream, existing, cancellationToken);
            return;
        }
        if (rangeFill)
        {
            try { await AttachRangeAsync(stream, transfer, cancellationToken); }
            catch { transfer.Stop(); throw; }
            return;
        }
        var reader = transfer.TryOpen(!protocol.IsPreparation);
        if (reader == null)
        {
            transfer.Stop();
            throw new IOException("The progressive media cache could not open a reader.");
        }
        var replacement = new ProgressiveContent(reader, transfer.PlaybackStarted, transfer.PrepareSourceAsync);
        foreach (var header in original.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        stream.Response.Content = replacement;
    }

    private static async Task AttachRangeAsync(ProtocolProviderStream stream, Transfer transfer, CancellationToken token)
    {
        var original = stream.Response.Content;
        var source = await original.ReadAsStreamAsync(token);
        if (!transfer.AttachRemoteReader()) return;
        var replacement = new RetainedRangeContent(new RetainedRangeStream(source, original, transfer.Activate, transfer.RemoteClosed));
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
        completedMedia.Dispose();
        lock (audioAllocationGate) { originalRootHandle?.Dispose(); originalRootHandle = null; }
    }

    private ProviderMediaFormat ReadFileMedia(string path, string mime, string container, ProviderMediaFormat? known = null)
    {
        var media = known ?? new ProviderMediaFormat(mime, container, "unknown");
        try
        {
            using var file = TagLib.File.Create(path);
            var properties = file.Properties;
            var codec = file is TagLib.Flac.File ? "flac" : file is TagLib.Mpeg.AudioFile ? "mp3" : media.Codec;
            media = new(mime, container, codec,
                properties.AudioBitrate is > 0 and <= int.MaxValue / 1000 ? properties.AudioBitrate * 1000 : media.Bitrate,
                properties.AudioSampleRate > 0 ? properties.AudioSampleRate : media.SampleRate,
                properties.BitsPerSample > 0 ? properties.BitsPerSample : media.BitDepth,
                properties.AudioChannels > 0 ? properties.AudioChannels : media.Channels);
        }
        catch (Exception) { }
        completedMedia.Set(path, new TrackMediaFacts(media, new FileInfo(path).Length), new MemoryCacheEntryOptions
        { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
        return media;
    }

    private static string Key(DownloadedSongMappingScope scope, long? revision, string provider, string track, bool originalDownload = false) =>
        $"{scope.Key}\n{revision}\n{provider}\n{track}\n{(originalDownload ? "original-download" : "playback")}";

    internal static string? CompletedFilePath(Stream stream) =>
        (stream is PinnedAudioStream pinned ? pinned.Source : stream) is FileStream file && File.Exists(file.Name) ? file.Name : null;

    internal static string? SeekRung(HttpContent content) => content is ProgressiveContent progressive
        ? progressive.Reader.CanSeek ? "progressive-cache" : "sequential-cache" : null;

    internal static Task WaitForRangeAsync(HttpContent content, string? rangeHeader, CancellationToken cancellationToken) =>
        content is ProgressiveContent progressive && StreamByteRanges.Parse(rangeHeader) is { } range
            ? progressive.Reader.WaitForRangeAsync(range, cancellationToken) : Task.CompletedTask;

    internal static CancellationToken PlaybackStarted(HttpContent content) =>
        content is ProgressiveContent progressive ? progressive.PlaybackStarted : CancellationToken.None;

    internal static void PrepareForConversion(HttpContent content, bool useDownloadCompletion = true)
    {
        if (content is ProgressiveContent progressive) progressive.Reader.UseDownloadCompletion(useDownloadCompletion);
    }

    internal static async Task PrepareForConversionAsync(HttpContent content, CancellationToken token, bool useDownloadCompletion = true)
    {
        if (content is not ProgressiveContent progressive) return;
        progressive.Reader.UseDownloadCompletion(useDownloadCompletion);
        await progressive.PrepareSource(token);
    }

    private sealed class ProgressiveContent(ProgressiveCachingStream reader, CancellationToken playbackStarted,
        Func<CancellationToken, Task> prepareSource) : StreamContent(reader)
    {
        public ProgressiveCachingStream Reader { get; } = reader;
        public CancellationToken PlaybackStarted { get; } = playbackStarted;
        public Func<CancellationToken, Task> PrepareSource { get; } = prepareSource;
    }

    private sealed class RetainedRangeContent(RetainedRangeStream reader) : StreamContent(reader);

    private sealed class RetainedRangeStream(Stream source, HttpContent original, Action started, Action closed) : Stream
    {
        private int disposed;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read > 0) started();
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read > 0) started();
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try { original.Dispose(); }
                finally { closed(); }
            }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Dispose(); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
        private HttpContent? original;
        private Stream? source;
        private readonly Func<CancellationToken, Task<HttpResponseMessage>>? fullResponseFactory;
        private HttpResponseMessage? openedFullResponse;
        private readonly Func<Task<Song?>> metadata;
        private readonly bool retain;
        public PlaybackFileFacts FileFacts { get; }
        private readonly bool preparation;
        private readonly long maximumBytes;
        private readonly bool supportsUpstreamRanges;
        private readonly CancellationTokenRegistration preparationCancellation;
        private volatile bool playbackOwned;
        private readonly CancellationTokenSource playbackStarted = new();
        public CancellationToken PlaybackStarted { get; }
        private readonly CancellationTokenSource cancel;
        private readonly ITimer idleTimer;
        private readonly ITimer? lifetimeTimer;
        private readonly bool originalDownload;
        private readonly bool retainedOriginal;
        private readonly AudioAllocation allocation;
        private Exception? failure;
        private readonly TaskCompletionSource ready = Signal();
        private readonly TaskCompletionSource sourceRequested = Signal();
        private readonly TaskCompletionSource<Exception?> sourceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource complete = Signal();
        private readonly object gate = new();
        private TaskCompletionSource changed = Signal();
        private string path;
        private long available;
        private long? total;
        private int readers;
        private bool finished;
        private bool downloaded;
        private bool failed;
        private bool retired;
        public ProviderMediaFormat Media { get; private set; }
        public Task Work { get; private set; } = Task.CompletedTask;

        public Transfer(ManagedTrackCacheService owner, string key, DownloadedSongMappingScope scope,
            string provider, string track, ProviderMediaFormat media, string cacheRoot, string path, FileStream output,
            HttpContent? original, Stream? source, long? total, Func<Task<Song?>> metadata, bool retain, CancellationToken stopping,
            PlaybackFileFacts fileFacts, CancellationToken preparationToken, bool preparation, long maximumBytes,
            bool supportsUpstreamRanges, bool originalDownload, AudioAllocation allocation,
            bool retainedOriginal, Func<CancellationToken, Task<HttpResponseMessage>>? fullResponseFactory)
        {
            this.owner = owner; this.key = key; this.scope = scope; this.provider = provider; this.track = track;
            FileFacts = fileFacts;
            Media = media; this.cacheRoot = cacheRoot; this.path = path; this.output = output;
            this.original = original; this.source = source; this.total = total; this.metadata = metadata; this.retain = retain;
            this.preparation = preparation;
            this.maximumBytes = maximumBytes;
            this.supportsUpstreamRanges = supportsUpstreamRanges;
            this.originalDownload = originalDownload;
            this.retainedOriginal = retainedOriginal;
            this.fullResponseFactory = fullResponseFactory;
            if (fullResponseFactory == null) sourceReady.TrySetResult(null);
            this.allocation = allocation;
            PlaybackStarted = playbackStarted.Token;
            cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            idleTimer = owner.clock.CreateTimer(_ => StopIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            lifetimeTimer = originalDownload ? owner.clock.CreateTimer(_ => Stop(), null,
                TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan) : null;
            preparationCancellation = preparationToken.Register(() =>
            {
                lock (gate) { if (!playbackOwned && !retired) cancel.Cancel(); }
            });
        }

        public void StartWorker() => Work = Task.Run(RunAsync);
        public void Stop()
        {
            lock (gate) { if (!retired) cancel.Cancel(); }
        }

        public bool CanFetchRange(long offset)
        {
            lock (gate) return supportsUpstreamRanges && !finished && offset >= available;
        }

        public ProgressiveCachingStream? TryOpen(bool playback)
        {
            lock (gate)
            {
                if (retired || failed || cancel.IsCancellationRequested) return null;
                FileStream file;
                try { file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous); }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                if (playback)
                {
                    playbackOwned = true;
                    playbackStarted.Cancel();
                }
                readers++;
                owner.AudioPin(allocation);
                var signal = changed;
                changed = Signal();
                signal.TrySetResult();
                idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return new ProgressiveCachingStream(file, Snapshot, Closed,
                    ProgressiveCachingStream.SeekWaitTimeout, Activate, ProgressiveCachingStream.InitialReadWaitTimeout,
                    timeProvider: owner.clock, maximumWaitTimeout: ProgressiveCachingStream.SeekWaitTimeout,
                    downloadState: DownloadSnapshot);
            }
        }

        public bool AttachRemoteReader()
        {
            lock (gate)
            {
                if (retired || failed || cancel.IsCancellationRequested) return false;
                playbackOwned = true;
                playbackStarted.Cancel();
                readers++;
                owner.AudioPin(allocation);
                idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return true;
            }
        }

        public void Activate()
        {
            lock (gate)
            {
                if (retired || failed || cancel.IsCancellationRequested) return;
                sourceRequested.TrySetResult();
                ready.TrySetResult();
            }
        }

        public async Task PrepareSourceAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fullResponseFactory != null && !sourceReady.Task.IsCompleted)
            {
                lock (gate)
                {
                    if (!retired && !failed && !cancel.IsCancellationRequested) sourceRequested.TrySetResult();
                }
            }
            var error = await sourceReady.Task.WaitAsync(token);
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        public void RemoteClosed() => Closed();

        private ProgressiveCacheReadState Snapshot()
        {
            lock (gate) return new(available, total, finished, failed, changed.Task, complete.Task, failure);
        }

        private ProgressiveCacheReadState DownloadSnapshot()
        {
            lock (gate) return new(available, total, downloaded || finished, failed, changed.Task,
                downloaded ? Task.CompletedTask : complete.Task, failure);
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
                owner.AudioClosed(allocation);
                if (--readers == 0 && !downloaded && !finished && !retired && (!retainedOriginal || !ready.Task.IsCompleted))
                    idleTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
                var signal = changed;
                changed = Signal();
                signal.TrySetResult();
            }
        }

        private void StopIfIdle()
        {
            lock (gate)
            {
                if (readers == 0 && !downloaded && !finished && !retired && (!retainedOriginal || !ready.Task.IsCompleted)) cancel.Cancel();
            }
        }

        private async Task RunAsync()
        {
            var published = false;
            var verified = false;
            using var abort = cancel.Token.Register(DisposeSource);
            try
            {
                if (fullResponseFactory != null)
                {
                    await sourceRequested.Task.WaitAsync(cancel.Token);
                    await OpenCompleteResponseAsync();
                }
                sourceReady.TrySetResult(null);
                await ready.Task.WaitAsync(cancel.Token);
                var input = source ?? throw new IOException("The complete media source is unavailable.");
                var content = original ?? throw new IOException("The complete media content is unavailable.");
                Task<Song?> metadataTask;
                try { metadataTask = retain ? metadata() : Task.FromResult<Song?>(null); }
                catch { metadataTask = Task.FromResult<Song?>(null); }
                // Observe a metadata failure even when the audio source fails first.
                _ = metadataTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                using var contentHash = retain ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancel.Token);
                    lock (gate) total ??= StreamDurationGuidance.DeclaredLength(content);
                    if (read == 0) break;
                    if (!playbackOwned && read > maximumBytes - available)
                        throw new IOException("The prepared media exceeds the cache budget.");
                    if (total is { } expected && read > expected - available)
                        throw new IOException("The media source exceeded its declared length.");
                    await owner.GrowAudioAsync(allocation, checked(available + read), cancel.Token);
                    contentHash?.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel.Token);
                    await output.FlushAsync(cancel.Token);
                    lock (gate) available = checked(available + read);
                    Advance();
                }
                if (available == 0 || total is { } length && available != length)
                    throw new IOException("The media source ended before its declared length.");
                allocation.ContentHash = contentHash == null ? null : Convert.ToHexStringLower(contentHash.GetHashAndReset());
                await output.DisposeAsync();
                lock (gate) { downloaded = true; total ??= available; }
                Advance();
                owner.AudioPathChanged(allocation, path);
                lock (owner.audioAllocationGate) allocation.Reserved = available;
                DisposeSource();
                FileFacts.Size = available;
                Song? song = null;
                try { song = await metadataTask.WaitAsync(cancel.Token); }
                catch (Exception) when (!cancel.IsCancellationRequested) { }
                var expectedSeconds = FileFacts.ExpectedDurationSeconds ?? FileFacts.Measured?.DurationSeconds ?? song?.PreciseDuration?.TotalSeconds ?? song?.Duration;
                var measured = await owner.mediaVerifier.VerifyFileAsync(path, Media, expectedSeconds, cancel.Token, FileFacts.DurationIsDerived);
                verified = true;
                allocation.VerifiedDuration = measured.DurationSeconds;
                Media = measured.Media;
                FileFacts.Media = Media;
                FileFacts.Measured = measured;
                Advance(end: true);
                if (!originalDownload && FileFacts.RecordAsync is { } record)
                {
                    try { await record(measured, cancel.Token); }
                    catch (Exception error) when (error is not OperationCanceledException || !cancel.IsCancellationRequested)
                    { owner.logger.LogDebug("Could not retain completed media facts ({ErrorType})", error.GetType().Name); }
                }
                if (retain) published = await PublishAsync(metadataTask);
                else
                {
                    complete.TrySetResult();
                    await RetainForPlaybackAsync();
                }
            }
            catch (Exception error)
            {
                sourceReady.TrySetResult(error);
                failure = error is LocalCapacityException ? error : null;
                if (!cancel.IsCancellationRequested && error is not LocalCapacityException) FileFacts.SourceFailed?.Invoke();
                Advance(end: true, failure: !verified);
                if (!cancel.IsCancellationRequested && !preparation && error is not LocalCapacityException)
                    owner.logger.LogWarning("The managed media transfer could not complete for {ProviderId}", provider);
            }
            finally
            {
                try { await output.DisposeAsync(); } catch (IOException) { }
                DisposeSource();
                lock (gate) { retired = true; playbackStarted.Dispose(); }
                if (!published) TryDelete(path);
                owner.AudioCompleted(allocation);
                owner.active.TryRemove(key, out _);
                complete.TrySetResult();
                preparationCancellation.Dispose();
                idleTimer.Dispose();
                lifetimeTimer?.Dispose();
                cancel.Dispose();
            }
        }

        private async Task OpenCompleteResponseAsync()
        {
            using var timeout = new CancellationTokenSource(ProgressiveCachingStream.InitialReadWaitTimeout, owner.clock);
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, timeout.Token);
            var pending = fullResponseFactory!(opening.Token);
            HttpResponseMessage response;
            try { openedFullResponse = response = await pending.WaitAsync(opening.Token); }
            catch
            {
                _ = pending.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            cancel.Token.ThrowIfCancellationRequested();
            if (!IsCompleteResponse(response)) throw new IOException("The media source did not return a complete original file.");
            var content = response.Content;
            var length = response.StatusCode == HttpStatusCode.PartialContent
                ? content.Headers.ContentRange?.Length : content.Headers.ContentLength;
            if (total is { } expected && length is { } actual && expected != actual)
                throw new IOException("The original file length changed between requests.");
            lock (gate) total ??= length;
            if (total is { } size) await owner.GrowAudioAsync(allocation, size, cancel.Token);
            original = content;
            source = await content.ReadAsStreamAsync(cancel.Token);
            if (PrefetchedStream.Facts(content) is { } measured)
            {
                Media = measured.Media;
                FileFacts.Media = measured.Media;
                FileFacts.Measured = measured;
            }
        }

        private void DisposeSource()
        {
            try
            {
                if (openedFullResponse != null) openedFullResponse.Dispose();
                else original?.Dispose();
            }
            catch (IOException) { }
        }

        private async Task RetainForPlaybackAsync()
        {
            while (true)
            {
                Task wait;
                bool idle;
                lock (gate)
                {
                    idle = readers == 0;
                    wait = changed.Task;
                }
                try
                {
                    if (idle) await wait.WaitAsync(TimeSpan.FromSeconds(10), owner.clock, cancel.Token);
                    else await wait.WaitAsync(cancel.Token);
                }
                catch (TimeoutException)
                {
                    lock (gate)
                    {
                        if (readers != 0) continue;
                        retired = true;
                        return;
                    }
                }
            }
        }

        private async Task<bool> PublishAsync(Task<Song?> metadataTask)
        {
            var existing = await owner.localLibrary.GetLocalPathForExternalSongAsync(scope, provider, track);
            if (existing != null && File.Exists(existing)) return false;
            Song? song = null;
            try
            {
                song = retainedOriginal
                    ? await metadataTask.WaitAsync(TimeSpan.FromSeconds(30), owner.clock, cancel.Token)
                    : await metadataTask.WaitAsync(cancel.Token);
            }
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
                owner.AudioPathChanged(allocation, final);
            }
            song.LocalPath = final;
            owner.completedMedia.Set(final, FileFacts.Measured ?? new TrackMediaFacts(Media, available),
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
            FileFacts.Media = Media;
            await owner.localLibrary.RegisterDownloadedSongAsync(scope, song, final);
            await owner.MarkAudioPublishedAsync(allocation, scope, provider, track, cancel.Token);
            if (!preparation)
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
               StreamByteRanges.HasValidContentRange(range, response.Content.Headers.ContentLength) &&
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
            "wav" => ".wav",
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
