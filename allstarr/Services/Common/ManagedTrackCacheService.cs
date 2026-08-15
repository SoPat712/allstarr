using System.Collections.Concurrent;
using System.Net;
using allstarr.Core.Capabilities;
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

    public async Task WrapAsync(
        ProtocolProviderStream stream,
        string providerId,
        string externalId,
        ProviderAudioQuality requestedQuality,
        bool headOnly,
        Func<Task<Song?>> metadataFactory,
        CancellationToken cancellationToken)
    {
        if (!IsCacheMode() ||
            headOnly ||
            requestedQuality != ProviderAudioQuality.Any ||
            !IsCompleteResponse(stream.Response)) return;

        var key = $"{providerId}\n{externalId}";
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
                    var existing = await localLibrary.GetLocalPathForExternalSongAsync(providerId, externalId);
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
                        await localLibrary.RegisterDownloadedSongAsync(song, finalPath);
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
            // Cleanup is retried by the normal cache TTL sweep.
        }
    }
}
