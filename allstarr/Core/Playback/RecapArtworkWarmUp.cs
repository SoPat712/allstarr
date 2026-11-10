using System.Threading.Channels;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Core.Playback;

public sealed class RecapArtworkWarmUp(RecapArtworkReader artwork, ILogger<RecapArtworkWarmUp> logger) : BackgroundService
{
    private sealed record Image(UserRecord Listener, string Artist, string? Title = null, string? Album = null, string? LibraryArtistId = null)
    {
        public MediaAssetIdentity Identity => RecapArtworkReader.Identity(Listener.Id, Artist, Title, Album, LibraryArtistId);
    }

    private readonly Channel<Image> pending = Channel.CreateUnbounded<Image>();
    private readonly HashSet<MediaAssetIdentity> queued = [];

    public void Queue(UserRecord listener, ListeningStats stats)
    {
        foreach (var song in stats.TopSongs) Add(new(listener, song.Artist, Title: song.Title));
        foreach (var album in stats.TopAlbums) Add(new(listener, album.Artist, Album: album.Album));
        foreach (var artist in stats.TopArtists.Concat(stats.NewArtists))
            Add(new(listener, artist.Artist, LibraryArtistId: artist.Id != null && !artist.Id.StartsWith("ext-", StringComparison.Ordinal) ? artist.Id : null));
    }

    private void Add(Image image)
    {
        lock (queued)
        {
            if (!queued.Add(image.Identity)) return;
            pending.Writer.TryWrite(image);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(PrepareAsync(stoppingToken), PrepareAsync(stoppingToken));

    private async Task PrepareAsync(CancellationToken token)
    {
        try
        {
            await foreach (var image in pending.Reader.ReadAllAsync(token))
            {
                try
                {
                    var user = image.Listener;
                    var context = new ProtocolExecutionContext(Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
                        user.BackendPrincipalId, new(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId,
                            user.DisplayName, user.IsAdmin), "recap-art-warm-up", DateTimeOffset.UtcNow.AddSeconds(30), token)
                    { IsReadOnly = true, UseStoredBackendCredentials = true };
                    await artwork.PrepareAsync(context, image.Artist, image.Title, image.Album, token, image.LibraryArtistId);
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is not OutOfMemoryException)
                { logger.LogDebug("Recap artwork warm-up skipped an image ({ErrorType})", error.GetType().Name); }
                finally { lock (queued) queued.Remove(image.Identity); }
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
