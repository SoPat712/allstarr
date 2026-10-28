using System.Text.Json.Nodes;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using allstarr.Core.Playlists;
using allstarr.Models.Domain;

namespace allstarr.Core.Protocols;

internal static class CleanOnlyPlaylistProjection
{
    internal static async Task<VirtualPlaylistReadModel> ApplyAsync(VirtualPlaylistReadModel playlist,
        ProtocolExecutionContext context, IProtocolProviderGateway? providers,
        Func<string, Task<bool>>? nativeAllowed = null)
    {
        if (context.Policy?.Preferences.ExplicitFilter != "CleanOnly" || providers == null)
            return ExternalTrackPresentation.ApplyPreferences(playlist, context.Policy);
        var tracks = new List<VirtualPlaylistTrack>();
        var replacements = new Dictionary<string, Song?>(StringComparer.Ordinal);
        foreach (var track in playlist.Tracks)
        {
            Song? nativeSource = null;
            if (playlist.ProjectionMode != PlaylistProjectionMode.Source && track.RouteKind == TrackRouteKind.Local)
            {
                var native = track.NativeEntryJson == null ? null : JsonNode.Parse(track.NativeEntryJson) as JsonObject;
                if (native != null && CleanOnlyProtocolContent.Rating(native, context.Protocol) is 0 or 3 ||
                    nativeAllowed != null && await nativeAllowed(track.BackendItemId))
                {
                    tracks.Add(track);
                    continue;
                }
                if (native != null && CleanOnlyProtocolContent.Rating(native, context.Protocol) == 1)
                    nativeSource = CleanOnlyProtocolContent.Song(native, context.Protocol);
                if (nativeSource == null && track.SourceMetadata?.IsExplicit != true) continue;
            }
            if (nativeSource == null && ExternalTrackPresentation.Explicitness(track) == false)
            {
                if (track.PlaybackExplicitContentLyrics is 0 or 3 || playlist.ProjectionMode == PlaylistProjectionMode.Source ||
                    !string.IsNullOrEmpty(track.RouteProviderId) && !string.IsNullOrEmpty(track.RouteExternalId) &&
                    track.RouteProviderId == track.SourceProviderId && track.RouteExternalId == track.SourceExternalId)
                    tracks.Add(track);
                else if (track.RouteKind == TrackRouteKind.External && track.RouteProviderId != null && track.RouteExternalId != null &&
                    await providers.GetStoredSongAsync(context, track.RouteProviderId, track.RouteExternalId) is { } selected &&
                    CleanRecordingPolicy.IsVerifiedClean(selected)) tracks.Add(track with { PlaybackExplicitContentLyrics = selected.ExplicitContentLyrics });
                continue;
            }
            if (nativeSource == null && track.SourceMetadata?.IsExplicit != true) continue;
            var source = nativeSource ?? new Song
            {
                Id = track.BackendItemId,
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album ?? "",
                Duration = track.DurationMilliseconds is { } duration ? (int)(duration / 1000) : null,
                ExplicitContentLyrics = 1
            };
            if (source.Duration is not > 0 || source.Album.Length == 0 || source.Artist.Length == 0) continue;
            if (!replacements.TryGetValue(source.Id, out var replacement))
            {
                try
                {
                    var candidates = await providers.SearchAsync(context, $"{source.Title} {source.Artist}", 20, 0, 0);
                    replacement = candidates.Songs.FirstOrDefault(candidate => CleanRecordingPolicy.IsCounterpart(source, candidate));
                }
                catch (Exception) when (!context.CancellationToken.IsCancellationRequested) { replacement = null; }
                replacements[source.Id] = replacement;
            }
            if (replacement == null) continue;
            tracks.Add(track with
            {
                BackendItemId = replacement.Id,
                Title = replacement.Title,
                Artist = replacement.Artist,
                Album = replacement.Album,
                AlbumArtist = replacement.AlbumArtist,
                DurationMilliseconds = replacement.Duration * 1000L,
                CoverArtReference = replacement.Id,
                RouteKind = TrackRouteKind.External,
                RouteProviderId = replacement.ExternalProvider,
                RouteExternalId = replacement.ExternalId,
                MatchState = TrackMatchState.Accepted,
                PlaybackExplicitContentLyrics = replacement.ExplicitContentLyrics,
                NativeEntryJson = null,
                NativePlaylistEntryId = null,
                OriginalItemId = source.Id
            });
        }
        return playlist with { Tracks = tracks };
    }
}
