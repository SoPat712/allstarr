using allstarr.Core.Matching;
using allstarr.Models.Domain;
using allstarr.Services.Local;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    private async Task<IReadOnlyList<PersonalMix>> PreferLocalCopiesAsync(Guid owner, PersonalMix[] mixes, CancellationToken token)
    {
        if (libraryIndex == null || identities == null) return mixes;
        var songs = mixes.SelectMany(mix => mix.Songs).Select(item => item.Song).Where(song => !song.IsLocal)
            .DistinctBy(song => song.Id).ToArray();
        if (songs.Length == 0) return mixes;
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var context = await ContextAsync(user, token);
        var recordings = await identities.ResolveRecordingsAsync(context.RequireActor(), songs.Select(song => song.Id).ToArray(), token);
        var lookups = songs.Select(song =>
        {
            var recording = recordings.GetValueOrDefault(song.Id);
            var parsed = LocalLibraryService.ParseExternalResource(song.Id);
            return new LibraryRecordingLookup(song.Id, recording?.Id ?? song.CanonicalRecordingId,
                recording?.Isrc ?? song.Isrc, parsed.provider ?? song.ExternalProvider, parsed.externalId ?? song.ExternalId);
        }).ToArray();
        var native = await libraryIndex.FindKnownRecordingTracksAsync(context, lookups, token);
        if (native.Count == 0) return mixes;
        Song Project(Song song)
        {
            if (song.IsLocal || !native.TryGetValue(song.Id, out var itemId)) return song;
            var local = SafeSong(song);
            local.Id = itemId;
            local.IsLocal = true;
            local.ExternalProvider = null;
            local.ExternalId = null;
            local.CanonicalRecordingId = recordings.GetValueOrDefault(song.Id)?.Id ?? song.CanonicalRecordingId;
            return local;
        }
        return mixes.Select(mix => mix with
        { Songs = mix.Songs.Select(item => item with { Song = Project(item.Song) }).ToArray() }).ToArray();
    }
}
