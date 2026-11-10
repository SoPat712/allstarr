using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;

namespace allstarr.Core.Releases;

public sealed record ReleaseArtistCandidate(string ProviderId, string ArtistId, string DisplayName,
    string ServiceName, string? ImageUrl, string[] Hints);
public sealed record ReleaseArtistChoices(FollowedReleaseArtist Artist, ReleaseArtistCandidate[] Candidates, ReleaseArtistCandidate? CurrentChoice = null);

public sealed partial class NewReleasesService
{
    private const string ChoiceKind = "library-artist-choice", CandidatesKind = "library-artist-candidates";
    private const string ContradictedIdentifierReason = "The library artist ID does not agree with this artist's songs. Choose the right artist.";
    private const string ConflictingArtistReason = "The saved artist does not agree with this artist's songs. Choose the right artist.";

    public async Task<ReleaseArtistChoices> ArtistCandidatesAsync(Guid owner, string libraryArtistId, CancellationToken token = default)
    {
        var artist = (await ListAsync(owner, token)).SingleOrDefault(item => SameLibraryId(item.LibraryArtistId, libraryArtistId))
            ?? throw new ArgumentException("A followed library artist is required.");
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var candidates = new List<ReleaseArtistCandidate>();
        ReleaseArtistCandidate? current = null;
        using var traffic = new HistoryRequestBudget();
        foreach (var source in (await FollowSourcesAsync(user, token, allConnected: true)).DistinctBy(item => item.Context.ProviderId))
        {
            var found = await source.Implementation.SearchArtistsAsync(source.Context, new(artist.DisplayName, new(5)));
            if (!found.IsSuccess) continue;
            foreach (var candidate in found.RequireValue().Items.Where(item => item.Id.ProviderId == source.Context.ProviderId)
                .DistinctBy(item => item.Id).Take(5))
            {
                var hints = new List<string>();
                var albums = await source.Implementation.GetArtistAlbumsAsync(source.Context, new(candidate.Id, new(3)));
                if (albums.IsSuccess) hints.AddRange(albums.RequireValue().Items.Select(item => item.Title).Distinct().Take(3));
                if (hints.Count < 2 && source.Descriptor.Hooks.Contains("getArtistTracks", StringComparer.Ordinal))
                {
                    var songs = await source.Implementation.GetArtistTracksAsync(source.Context, new(candidate.Id, new(3)));
                    if (songs.IsSuccess) hints.AddRange(songs.RequireValue().Items.Select(item => item.Title));
                }
                candidates.Add(new(candidate.Id.ProviderId, candidate.Id.Value, candidate.Name, source.Provider.DisplayName,
                    candidate.Artwork?.PublicUri?.ToString(), hints.Distinct().Take(3).ToArray()));
            }
        }
        current = candidates.FirstOrDefault(item => item.ProviderId == artist.ProviderId && item.ArtistId == artist.ArtistId);
        if (current == null && artist.ProviderId != "jellyfin")
        {
            var source = await RouteAsync(user, artist, token);
            current = new(artist.ProviderId, artist.ArtistId, "Current service artist", source?.Provider.DisplayName ?? "Current music service", null, []);
            if (source != null)
            {
                var id = new ProviderExternalResourceId(source.Context.ProviderId, ProviderResourceKind.Artist, artist.ArtistId);
                var details = await source.Implementation.GetArtistAsync(source.Context, new(id));
                if (details.IsSuccess && details.RequireValue().Id == id)
                {
                    var value = details.RequireValue();
                    current = current with { DisplayName = value.Name, ImageUrl = value.Artwork?.PublicUri?.ToString() };
                }
            }
        }
        await Gate.WaitAsync(token);
        try
        {
            var allowed = current == null ? candidates.ToArray() : candidates.Append(current)
                .DistinctBy(item => (item.ProviderId, item.ArtistId)).ToArray();
            await SaveAsync(db, user, CandidatesKind, ChoiceKey(user, libraryArtistId), allowed, token);
            await db.SaveChangesAsync(token);
        }
        finally { Gate.Release(); }
        return new(artist, candidates.ToArray(), current);
    }

    public async Task<FollowedReleaseArtist> ChooseArtistAsync(Guid owner, string libraryArtistId,
        string? providerId, string? artistId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(libraryArtistId) || libraryArtistId.Length > 500 || libraryArtistId.Any(char.IsControl) ||
            (providerId == null) != (artistId == null)) throw new ArgumentException("An artist choice is required.");
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var follows = (await FollowsAsync(db, user, token)).ToList();
            var position = follows.FindIndex(item => SameLibraryId(item.LibraryArtistId, libraryArtistId));
            if (position < 0) throw new ArgumentException("A followed library artist is required.");
            var artist = follows[position];
            var candidates = Read<ReleaseArtistCandidate[]>(await LatestAsync(db, CandidatesKind, ChoiceKey(user, libraryArtistId), token)) ?? [];
            if (providerId != null && !candidates.Any(item => item.ProviderId == providerId && item.ArtistId == artistId) &&
                !(artist.ProviderId == providerId && artist.ArtistId == artistId))
                throw new ArgumentException("Choose an artist from this listener's candidates.");
            var before = await ReadForEventAsync(owner, token);
            var chosen = artist with
            {
                ProviderId = providerId ?? "jellyfin",
                ArtistId = artistId ?? artist.LibraryArtistId!,
                ListenerConfirmed = true,
                FromSongLinks = false,
                FromCatalogEvidence = false,
                ResolutionReason = null
            };
            await SaveAsync(db, user, ChoiceKind, ChoiceKey(user, libraryArtistId), chosen, token);
            follows[position] = chosen;
            await SaveAsync(db, user, FollowsKind, Scope(user), follows.ToArray(), token);
            await EnsureScheduleAsync(db, user, token);
            await db.SaveChangesAsync(token);
            await PublishInboxChangeAsync(owner, before, token);
            return chosen;
        }
        finally { Gate.Release(); }
    }

    private async Task<FollowedReleaseArtist?> ConfirmedArtistAsync(Guid owner, FollowedReleaseArtist artist, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var choice = Read<FollowedReleaseArtist>(await LatestAsync(db, ChoiceKind, ChoiceKey(user, artist.LibraryArtistId!), token));
        return choice == null ? null : choice with
        {
            DisplayName = artist.DisplayName,
            LibraryArtistId = artist.LibraryArtistId,
            LibraryImageTag = artist.LibraryImageTag,
            LibraryMusicBrainzArtistId = artist.LibraryMusicBrainzArtistId
        };
    }

    private static string ChoiceKey(UserRecord user, string nativeId) => Hash(Scope(user) + ":" +
        (Guid.TryParse(nativeId, out var id) ? id.ToString("N") : nativeId));
}
