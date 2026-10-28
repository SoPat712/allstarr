using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Settings;
using allstarr.Models.Domain;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private async Task<bool> AllowsCleanPlaybackAsync(ProtocolExecutionContext protocol,
        ProviderExternalResourceId track, ProviderExecutionContext? execution = null)
    {
        var policy = effectivePolicies == null ? protocol.Policy : protocol.Actor?.EffectiveUserId is { } user
            ? await effectivePolicies.ResolveForUserAsync(user, protocol.CancellationToken)
            : await effectivePolicies.ResolveAsync(protocol.CancellationToken);
        if (policy?.Preferences.ExplicitFilter != "CleanOnly") return true;
        var actor = protocol.RequireActor();
        Song? song = await ReadCachedSongAsync(actor, track.ProviderId, track.Value, execution?.Account);
        if (song == null && keptFiles != null)
            song = await keptFiles.FindSongAsync(actor, ProtocolItemId(track), protocol.CancellationToken);
        if (song == null && identities != null)
            song = (await identities.GetStoredSongsAsync(actor, ProtocolItemId(track), protocol.CancellationToken))?
                .FirstOrDefault(item => NormalizeProvider(item.ExternalProvider) == track.ProviderId && item.ExternalId == track.Value);
        if (song != null) return CleanRecordingPolicy.IsVerifiedClean(song);
        if (track.ProviderId == "allstarr") return false;
        var routed = await PlanExactAsync<IProviderMetadataCapability>(protocol, track.ProviderId,
            ProviderCapabilityKind.Metadata, "clean-playback-evidence");
        if (routed.Candidate == null) return false;
        var outcome = await routed.Candidate.Implementation.GetTrackAsync(routed.Candidate.Context, new(track));
        if (!outcome.IsSuccess || outcome.RequireValue().Id != track) return false;
        song = Map(outcome.RequireValue());
        await CacheSongsAsync(routed.Candidate.Context, [song], true);
        return CleanRecordingPolicy.IsVerifiedClean(song);
    }

    private async Task<List<Song>> ProjectCleanPlaylistAsync(ProtocolExecutionContext protocol,
        IReadOnlyList<Song> songs, EffectiveProviderPolicySnapshot? policy)
    {
        if (policy?.Preferences.ExplicitFilter != "CleanOnly")
            return songs.Where(song => policy?.Includes(song) != false).ToList();
        var projected = new List<Song>();
        var lookups = new Dictionary<string, Song?>(StringComparer.Ordinal);
        foreach (var song in songs)
        {
            if (CleanRecordingPolicy.IsVerifiedClean(song)) { projected.Add(song); continue; }
            if (song.ExplicitContentLyrics != 1) continue;
            var replacement = songs.FirstOrDefault(candidate => CleanRecordingPolicy.IsCounterpart(song, candidate));
            if (replacement == null && !string.IsNullOrWhiteSpace(song.Artist) && !string.IsNullOrWhiteSpace(song.Album) && song.Duration is > 0)
            {
                var key = $"{song.Title}|{song.Artist}|{song.Album}|{song.Duration}";
                if (!lookups.TryGetValue(key, out replacement))
                {
                    try
                    {
                        var result = await SearchAsync(protocol, $"{song.Title} {song.Artist}", 20, 0, 0);
                        replacement = result.Songs.FirstOrDefault(candidate => CleanRecordingPolicy.IsCounterpart(song, candidate));
                    }
                    catch (Exception) when (!protocol.CancellationToken.IsCancellationRequested) { replacement = null; }
                    lookups[key] = replacement;
                }
            }
            if (replacement != null)
            {
                var copy = System.Text.Json.JsonSerializer.Deserialize<Song>(System.Text.Json.JsonSerializer.Serialize(replacement))!;
                copy.OriginalItemId = song.Id;
                projected.Add(copy);
            }
        }
        return projected;
    }

    private static IEnumerable<Album> CleanAlbums(IEnumerable<Album> albums, EffectiveProviderPolicySnapshot? policy) =>
        policy?.Preferences.ExplicitFilter != "CleanOnly" ? albums : albums.Where(album =>
            album.Songs.Count > 0 ? album.Songs.Any(CleanRecordingPolicy.IsVerifiedClean) : album.IsExplicit == false);
}
