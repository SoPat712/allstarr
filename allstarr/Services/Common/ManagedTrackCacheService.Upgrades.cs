using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Music;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Services.Common;

public sealed partial class ManagedTrackCacheService
{
    private Task upgradeWork = Task.CompletedTask;

    internal Task UpgradeCompletion { get { lock (lifecycle) return upgradeWork; } }

    internal static ProtocolExecutionContext UpgradeContext(ProtocolExecutionContext context, CancellationToken token) =>
        new(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId, context.Principal,
            context.CorrelationId, DateTimeOffset.UtcNow.AddMinutes(5), token, context.Client)
        { Policy = context.Policy, IsPreparation = true, RequestedItemId = context.RequestedItemId };

    internal Task ScheduleUpgrade(ProtocolExecutionContext context, ProviderExternalResourceId track,
        DownloadedSongMappingScope scope, long? revision,
        Func<CancellationToken, Task<ProtocolProviderStream?>> open,
        Func<ProtocolProviderStream, CancellationToken, Task<Song?>> metadata)
    {
        lock (lifecycle)
        {
            if (disposed || shutdown.IsCancellationRequested || !upgradeWork.IsCompleted ||
                !CanPrepare(context) || context.IsReadOnly || context.IsPreparation || context.IsOriginalDownload)
                return Task.CompletedTask;
            return upgradeWork = Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5), clock);
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, timeout.Token);
                var token = stopping.Token;
                try
                {
                    var oldPath = await localLibrary.GetLocalPathForExternalSongAsync(scope, track.ProviderId, track.Value);
                    if (oldPath == null || !SafeCachePath(oldPath)) return;
                    using var oldFile = await OpenPinnedAudioAsync(oldPath, token, preparation: true);
                    var identity = Identity(oldFile is PinnedAudioStream pinned ? pinned.Source : oldFile);
                    var previous = await ReadCachedMediaAsync(track, scope, token, revision);
                    if (previous == null || identity?.LinkCount != 1 || !await HasPreparationSpaceAsync(token)) return;
                    var stream = await open(token);
                    if (stream == null) return;
                    using var response = stream.Response;
                    if (!response.IsSuccessStatusCode || PlaybackStartPolicy.CompareQuality(stream.FileFacts.Media ?? stream.Lease.Media, previous) <= 0) return;
                    var background = UpgradeContext(context, token);
                    await WrapAsync(stream, background, stream.ServingProviderId, stream.ServingExternalId!, false,
                        () => metadata(stream, token), token, previous);
                    await PrepareForConversionAsync(response.Content, token);
                    await response.Content.CopyToAsync(Stream.Null, token);
                    var source = new ProviderExternalResourceId(stream.ServingProviderId, ProviderResourceKind.Track, stream.ServingExternalId!);
                    var sourceScope = ScopeFor(background, stream.ServingAccountId, stream.EffectiveQuality, stream.ServingAccountRevision);
                    await TransferCompletionAsync(source, sourceScope, stream.ServingAccountRevision).WaitAsync(token);
                    var replacement = await localLibrary.GetLocalPathForExternalSongAsync(sourceScope, source.ProviderId, source.Value);
                    if (replacement == null || replacement == oldPath || !File.Exists(replacement) ||
                        PlaybackStartPolicy.CompareQuality(await ReadCachedMediaAsync(source, sourceScope, token, stream.ServingAccountRevision), previous) <= 0) return;
                    await RetireReplacedFileAsync(oldPath, identity, scope, track.ProviderId, track.Value, token);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    logger.LogDebug("Managed cache upgrade did not complete ({ErrorType})", error.GetType().Name);
                }
            });
        }
    }

    private async Task RetireReplacedFileAsync(string path, ManagedFileSystemIdentity? identity,
        DownloadedSongMappingScope scope, string provider, string track, CancellationToken token)
    {
        if (identity?.LinkCount != 1 || !SafeCachePath(path)) return;
        await audioBudgetGate.WaitAsync(token);
        try
        {
            bool Remove()
            {
                if (!SafeCachePath(path) || !Same(identity, Identity(path))) return false;
                File.Delete(path);
                return true;
            }
            var mapping = cacheMappings == null ? null : await cacheMappings.FindAsync(scope, provider, track, token);
            var removed = mapping?.LocalPath == path
                ? await cacheMappings!.RemoveCacheFileAsync(mapping, Remove, token) : Remove();
            if (!removed) return;
            completedMedia.Remove(path);
            publishedIdentities.TryRemove(path, out _);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.LogDebug("Replaced cache file is still in use ({ErrorType})", error.GetType().Name); }
        finally { audioBudgetGate.Release(); }
    }
}
