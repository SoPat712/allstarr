using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryArtistEvidence(string ProviderId, string ArtistId, string Name, int SharedSongs,
    IReadOnlyList<string> SongTitles, bool SameName)
{
    public string Reason => SharedSongs == 0 ? SameName ? "same name, no shared songs" : "song credits disagree" :
        $"{(SameName ? "same name" : "name differs")}, {SharedSongs} shared song{(SharedSongs == 1 ? "" : "s")}" +
        (!SameName && SharedSongs < 2 ? " (needs 2)" : " (no majority of shared songs)");
}

public sealed record LibraryArtistReviewParts(IReadOnlyList<LibraryArtistLink> Songs, IReadOnlyList<LibraryArtistFix> Fixes,
    IReadOnlyList<LibraryArtistEvidence> Candidates, IReadOnlyList<LibraryArtistEvidence> Evidence, DateTimeOffset? CheckedAt);

public sealed partial class LibraryArtistIndex
{
    private const string CandidateKind = "library-artist-candidate";

    public async Task<IReadOnlyDictionary<string, int>> SongCountsAsync(Guid owner, IReadOnlyCollection<string> names, CancellationToken token)
    {
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return new Dictionary<string, int>();
        await using var db = await factory.CreateDbContextAsync(token);
        var tracks = await LibraryTrackAccess.Query(db, access).Select(track => new { track.BackendItemId, track.Artist, track.AlbumArtist }).ToArrayAsync(token);
        var counts = tracks.SelectMany(track => new[] { track.Artist, track.AlbumArtist ?? "" }
                .Concat(FuzzyMatcher.SplitCredits(track.Artist)).Concat(FuzzyMatcher.SplitCredits(track.AlbumArtist ?? ""))
                .Select(FuzzyMatcher.NormalizeForMatching).Where(name => name.Length > 0).Distinct().Select(name => (Name: name, track.BackendItemId)))
            .GroupBy(item => item.Name).ToDictionary(group => group.Key, group => group.Select(item => item.BackendItemId).Distinct().Count());
        return names.Distinct(StringComparer.Ordinal).ToDictionary(name => name, name => counts.GetValueOrDefault(FuzzyMatcher.NormalizeForMatching(name)));
    }

    public async Task RecordCandidatesAsync(Guid owner, IReadOnlyCollection<string> libraryNames,
        IReadOnlyCollection<Artist> serviceArtists, CancellationToken token = default)
    {
        var artists = serviceArtists.Where(artist => !string.IsNullOrWhiteSpace(artist.ExternalProvider) &&
            !string.IsNullOrWhiteSpace(artist.ExternalId) && artist.ExternalId.Length <= 300 && Usable(artist.ExternalId) &&
            emptyArtists?.IsEmpty(artist) != true).ToArray();
        var names = libraryNames.Where(ValidName).Distinct(StringComparer.Ordinal).Where(name => artists.Any(artist =>
            FuzzyMatcher.NormalizeForMatching(artist.Name) == FuzzyMatcher.NormalizeForMatching(name))).ToArray();
        if (names.Length == 0) return;
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return;
        await using var db = await factory.CreateDbContextAsync(token);
        var fixes = await FixesAsync(db, owner, access.Context, names, token);
        foreach (var name in names)
        {
            await Gate.WaitAsync(token);
            try
            {
                var key = HouseholdKey(access.Context, name);
                var previous = await db.ExternalMetadataSnapshots.AsNoTracking().Where(row => row.ProviderId == "allstarr" &&
                    row.ResourceKind == CandidateKind && row.ExternalIdHash == key)
                    .OrderByDescending(row => row.SnapshotVersion).FirstOrDefaultAsync(token);
                var saved = ReadCandidates(previous?.PayloadJson).ToList();
                var unseen = artists.Where(artist => FuzzyMatcher.NormalizeForMatching(artist.Name) == FuzzyMatcher.NormalizeForMatching(name) &&
                    !saved.Any(item => item.ProviderId == NormalizeProvider(artist.ExternalProvider!) && item.ArtistId == artist.ExternalId && item.Name == artist.Name)).ToArray();
                if (unseen.Length == 0) continue;
                var songs = await ComputedAsync(db, owner, access, name, token);
                var changed = false;
                foreach (var artist in unseen)
                {
                    var provider = NormalizeProvider(artist.ExternalProvider!);
                    var id = artist.ExternalId!;
                    if (songs.Any(link => link.ProviderId == provider && link.ArtistId == id) ||
                        (fixes.GetValueOrDefault(name) ?? []).Any(fix => fix.ProviderId == provider && fix.ArtistId == id)) continue;
                    var candidate = new LibraryArtistEvidence(provider, id, artist.Name, 0, [], true);
                    var old = saved.FindIndex(item => item.ProviderId == provider && item.ArtistId == id);
                    if (old >= 0 && saved[old].Name == candidate.Name) continue;
                    if (old >= 0) saved[old] = candidate;
                    else saved.Add(candidate);
                    changed = true;
                }
                if (!changed) continue;
                await SaveAsync(db, access.Context, CandidateKind, key, saved, "1", previous, token);
            }
            finally { Gate.Release(); }
        }
    }

    public async Task<IReadOnlyDictionary<string, LibraryArtistReviewParts>> ReadReviewAsync(Guid owner,
        IReadOnlyCollection<string> libraryNames, CancellationToken token = default, bool detail = false)
    {
        var names = libraryNames.Where(ValidName).Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, LibraryArtistReviewParts>(StringComparer.Ordinal);
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return result;
        if (names.Length == 0) return result;
        if (detail) await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var saved = detail ? null : await ReadSavedAsync(db, owner, access.Context, names, token);
            var keysByName = names.ToDictionary(name => name, name => HouseholdKey(access.Context, name), StringComparer.Ordinal);
            var keys = keysByName.Values.Distinct().ToArray();
            var rows = await db.ExternalMetadataSnapshots.AsNoTracking().Where(row => row.ProviderId == "allstarr" &&
                row.ResourceKind == CandidateKind &&
                keys.Contains(row.ExternalIdHash)).ToArrayAsync(token);
            var candidatesByKey = rows.GroupBy(row => row.ExternalIdHash, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => ReadCandidates(group.MaxBy(row => row.SnapshotVersion)?.PayloadJson), StringComparer.Ordinal);
            var fixes = await FixesAsync(db, owner, access.Context, names, token);
            foreach (var name in names)
            {
                var candidates = candidatesByKey.GetValueOrDefault(keysByName[name]) ?? [];
                var evidence = new List<LibraryArtistEvidence>();
                var songs = detail ? await ComputedAsync(db, owner, access, name, token, evidence) : saved!.GetValueOrDefault(name) ?? [];
                var decisions = fixes.GetValueOrDefault(name) ?? [];
                result[name] = new(songs, decisions, candidates.Where(candidate => !songs.Any(link => link.ProviderId == candidate.ProviderId &&
                    link.ArtistId == candidate.ArtistId) && !decisions.Any(fix => fix.ProviderId == candidate.ProviderId &&
                    fix.ArtistId == candidate.ArtistId) && emptyArtists?.IsEmpty(new Artist
                    {
                        ExternalProvider = candidate.ProviderId,
                        ExternalId = candidate.ArtistId
                    }) != true).ToArray(), evidence, clock.UtcNow);
            }
        }
        finally { if (detail) Gate.Release(); }
        return result;
    }

    private static IReadOnlyList<LibraryArtistEvidence> ReadCandidates(string? payload)
    {
        try { return JsonSerializer.Deserialize<LibraryArtistEvidence[]>(payload ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }
}
