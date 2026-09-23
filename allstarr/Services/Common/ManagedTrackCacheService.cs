using System.Collections.Concurrent;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Local;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

public sealed class ManagedTrackCacheService(
    IConfiguration configuration,
    IOptions<SubsonicSettings> settings,
    ILocalLibraryService localLibrary,
    ILogger<ManagedTrackCacheService> logger)
{
    private readonly ConcurrentDictionary<string, byte> active = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProtocolProviderStream?> TryOpenAsync(ProviderExternalResourceId track,
        DownloadedSongMappingScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = await localLibrary.GetLocalPathForExternalSongAsync(
            scope, track.ProviderId, track.Value);
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
            return new ProtocolProviderStream(response, new ProviderStreamLease("managed-cache",
                new Uri("https://allstarr.invalid/managed-cache"), DateTimeOffset.UtcNow.AddHours(1),
                true, true, new ProviderMediaFormat(mime, extension, extension),
                ProviderStreamRetryBehavior.DoNotRetry), track.ProviderId, track.Value,
                scope.ProviderAccountId, IsCached: true, EffectiveQuality: scope.AudioQuality);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task WrapAsync(
        ProtocolProviderStream stream,
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        bool headOnly,
        Func<Task<Song?>> metadataFactory,
        CancellationToken cancellationToken)
    {
        if (stream.IsCached || !IsCacheMode() ||
            headOnly ||
            !IsCompleteResponse(stream.Response)) return;
        var actor = protocol.Actor;
        if (actor == null) return;
        var scope = new DownloadedSongMappingScope(
            actor.TenantId,
            stream.ServingAccountId,
            protocol.LibraryScopeId,
            stream.EffectiveQuality);

        // Older callers without a resolved identity must never cache fallback bytes as the requested track.
        if (stream.ServingExternalId == null && stream.ServingProviderId != providerId) return;
        providerId = stream.ServingProviderId;
        externalId = stream.ServingExternalId ?? externalId;

        var key = $"{scope.Key}\n{providerId}\n{externalId}";
        if (!active.TryAdd(key, 0)) return;

        var cacheRoot = Path.Combine(
            configuration["Library:DownloadPath"] ?? "./downloads",
            "cache");
        var incomingRoot = Path.Combine(cacheRoot, ".incoming");
        var partialPath = Path.Combine(incomingRoot, $"provider-{Guid.NewGuid():N}.partial");
        FileStream? output = null;
        try
        {
            Directory.CreateDirectory(incomingRoot);
            output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception)
        {
            output?.Dispose();
            active.TryRemove(key, out _);
            logger.LogWarning(exception, "Could not open the managed track cache for {ProviderId}", providerId);
            return;
        }

        var originalContent = stream.Response.Content;
        Stream source;
        try
        {
            source = await originalContent.ReadAsStreamAsync(cancellationToken);
        }
        catch
        {
            await output.DisposeAsync();
            TryDelete(partialPath);
            active.TryRemove(key, out _);
            throw;
        }

        Task<Song?> metadataTask;
        try
        {
            metadataTask = metadataFactory();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Track metadata was unavailable while caching {ProviderId}", providerId);
            metadataTask = Task.FromResult<Song?>(null);
        }
        _ = metadataTask.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var mediaType = originalContent.Headers.ContentType?.MediaType ?? stream.Lease.Media.MimeType;
        var replacement = new StreamContent(new ProgressiveCachingStream(
            source,
            output,
            mediaType,
            async () =>
            {
                originalContent.Dispose();
                try
                {
                    var existing = await localLibrary.GetLocalPathForExternalSongAsync(
                        scope, providerId, externalId);
                    if (existing != null && File.Exists(existing))
                    {
                        TryDelete(partialPath);
                        return;
                    }

                    Song? song = null;
                    try
                    {
                        song = await metadataTask;
                    }
                    catch (Exception exception)
                    {
                        logger.LogDebug(exception, "Track metadata was unavailable while publishing {ProviderId}", providerId);
                    }
                    song ??= new Song
                    {
                        Id = $"ext-{providerId}-song-{externalId}",
                        Title = externalId,
                        Artist = "Unknown artist",
                        Album = "Unknown album"
                    };
                    song.ExternalProvider = providerId;
                    song.ExternalId = externalId;

                    if (string.Equals(mediaType, "audio/flac", StringComparison.OrdinalIgnoreCase))
                        await StripFlacId3PrefixAsync(partialPath);

                    var finalPath = PathHelper.BuildTrackPath(
                        cacheRoot,
                        song.AlbumArtist ?? song.Artist,
                        song.Album,
                        song.Title,
                        song.Track,
                        Extension(stream.Lease.Media),
                        providerId,
                        externalId);
                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                    finalPath = PathHelper.ResolveUniquePath(finalPath);
                    File.Move(partialPath, finalPath);
                    try
                    {
                        song.LocalPath = finalPath;
                        await localLibrary.RegisterDownloadedSongAsync(scope, song, finalPath);
                    }
                    catch
                    {
                        TryDelete(finalPath);
                        throw;
                    }
                    logger.LogInformation("Published completed provider stream to the managed track cache for {ProviderId}", providerId);
                }
                catch (Exception exception)
                {
                    TryDelete(partialPath);
                    logger.LogWarning(exception, "Could not publish the completed provider stream for {ProviderId}", providerId);
                }
                finally
                {
                    active.TryRemove(key, out _);
                }
            },
            () =>
            {
                originalContent.Dispose();
                TryDelete(partialPath);
                active.TryRemove(key, out _);
            }));
        foreach (var header in originalContent.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        stream.Response.Content = replacement;
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

    private static async Task StripFlacId3PrefixAsync(string path)
    {
        var normalized = path + ".normalized";
        try
        {
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length < 14) return;
                var header = new byte[10];
                await input.ReadExactlyAsync(header);
                if (!header.AsSpan(0, 3).SequenceEqual("ID3"u8) ||
                    header[3] is < 2 or > 4 ||
                    ((header[6] | header[7] | header[8] | header[9]) & 0x80) != 0) return;

                var tagSize = 10L + (header[6] << 21) + (header[7] << 14) +
                    (header[8] << 7) + header[9] +
                    (header[3] == 4 && (header[5] & 0x10) != 0 ? 10 : 0);
                if (tagSize > 1024 * 1024 || tagSize + 4 > input.Length) return;
                input.Position = tagSize;
                var marker = new byte[4];
                await input.ReadExactlyAsync(marker);
                if (!marker.AsSpan().SequenceEqual("fLaC"u8)) return;

                input.Position = tagSize;
                await using var output = new FileStream(normalized, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }
            File.Move(normalized, path, overwrite: true);
        }
        finally
        {
            TryDelete(normalized);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup is retried by the normal cache TTL sweep.
        }
    }
}
