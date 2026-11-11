using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Local;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    private sealed record RecordingIdentityCacheEntry(CanonicalRecordingIdentity? Recording);

    private async Task<IReadOnlyDictionary<string, CanonicalRecordingIdentity?>> RecordingIdentitiesAsync(
        AllstarrDbContext db, ProtocolExecutionContext context, string[] ids, CancellationToken token)
    {
        if (recordingCache?.IsEnabled != true) return await identities!.ResolveRecordingsAsync(context.RequireActor(), ids, token);
        var scope = db.FeedReadKey(context, "library-recording-identities");
        var result = new Dictionary<string, CanonicalRecordingIdentity?>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in ids)
        {
            var cached = await recordingCache.GetAsync<RecordingIdentityCacheEntry>(scope + ":" + Hash(id));
            if (cached == null) missing.Add(id);
            else result[id] = cached.Recording;
        }
        if (missing.Count > 0)
        {
            var resolved = await identities!.ResolveRecordingsAsync(context.RequireActor(), missing, token);
            foreach (var id in missing) result[id] = resolved.GetValueOrDefault(id);
            if (scope == db.FeedReadKey(context, "library-recording-identities"))
                foreach (var id in missing)
                    await recordingCache.SetAsync(scope + ":" + Hash(id), new RecordingIdentityCacheEntry(result[id]), TimeSpan.FromMinutes(10));
        }
        return result;
    }

    private async Task<IReadOnlyList<PersonalMix>> PreferLocalCopiesAsync(Guid owner, PersonalMix[] mixes, CancellationToken token)
    {
        if (libraryIndex == null || identities == null) return mixes;
        var songs = mixes.SelectMany(mix => mix.Songs).Select(item => item.Song).Where(song => !song.IsLocal)
            .DistinctBy(song => song.Id).ToArray();
        if (songs.Length == 0) return mixes;
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var context = await ContextAsync(user, token);
        var recordings = await RecordingIdentitiesAsync(db, context, songs.Select(song => song.Id).ToArray(), token);
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
