using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Core.Playback;

public sealed class PlaybackLyricsPrefetch(IPlaybackTrackResolver tracks,
    IProtocolLyricsResolver lyrics, IBackendLibraryAccessResolver libraryAccess) : IPlaybackLyricsPrefetch
{
    public async Task PrefetchAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var access = await libraryAccess.ResolveUserAsync(payload.Scope.OwnerUserId, timeout.Token);
        var viewer = access.Context;
        if (!access.Access.Succeeded || viewer?.Actor?.EffectiveUserId != payload.Scope.OwnerUserId ||
            viewer.BackendInstanceId != payload.Scope.BackendInstanceId ||
            viewer.Protocol.ToString().ToLowerInvariant() != payload.Scope.Protocol) return;
        var track = await tracks.ResolveAsync(payload, timeout.Token);
        if (track == null) return;
        var context = new ProtocolExecutionContext(viewer.Protocol, viewer.BackendInstanceId,
            viewer.VerifiedBackendPrincipalId, viewer.Principal, viewer.CorrelationId, viewer.Deadline,
            timeout.Token, viewer.Client)
        { Policy = viewer.Policy, RequestedItemId = payload.ItemId };
        var prefix = track.ProviderId + ":";
        var externalId = track.ProviderTrackReference?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? track.ProviderTrackReference[prefix.Length..] : null;
        var song = MusicLyrics.ForLookup(new Song
        {
            Title = track.Title,
            Artist = track.Artist,
            Artists = [track.Artist],
            Album = track.Album ?? "",
            Duration = track.DurationMilliseconds is { } duration ? (int)Math.Clamp(duration / 1000, 0, 86_400) : null,
            SpotifyId = track.ProviderId == "spotify" ? externalId : null
        });
        await lyrics.FindAsync(context, song, track.CanonicalRecordingId?.ToString("D") ?? payload.ItemId,
            track.ProviderId, externalId, song.SpotifyId);
    }
}
