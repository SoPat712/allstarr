using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Migrations;

public sealed record MigrationResourceCandidate(string ProviderId, string ResourceId, string Title, string? Artist,
    int? Year, int? TrackCount, string? ArtworkUrl, string[] Albums);
public sealed class MigrationResourceEntry
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public ProviderLibraryResource Source { get; set; } = null!;
    public string State { get; set; } = "unmatched";
    public List<MigrationResourceCandidate> Candidates { get; set; } = [];
    public MigrationResourceCandidate? Chosen { get; set; }
    public bool Applied { get; set; }
}

public sealed partial class CollectionMigrationService
{
    public async Task ChooseResourceAsync(ProtocolExecutionContext execution, Guid id, Guid collectionId, Guid entryId,
        string? providerId, string? resourceId, bool skip, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var (record, run) = await LoadAsync(db, execution, id, token);
        if (run.State is not ("review" or "completed")) throw new InvalidOperationException();
        var row = await db.Set<CollectionMigrationCollectionRecord>().SingleOrDefaultAsync(item => item.RunId == id && item.Id == collectionId, token)
            ?? throw new KeyNotFoundException();
        var collection = Read<MigrationCollection>(row.PayloadJson);
        var entry = collection.Resources?.SingleOrDefault(item => item.Id == entryId) ?? throw new KeyNotFoundException();
        if (entry.Applied) throw new InvalidOperationException();
        var choice = skip ? null : entry.Candidates.SingleOrDefault(item => item.ProviderId == providerId && item.ResourceId == resourceId)
            ?? throw new ArgumentException("Choose a listed match.");
        entry.Chosen = choice;
        entry.State = skip ? "skipped" : "accepted";
        row.PayloadJson = JsonSerializer.Serialize(collection);
        await SaveAsync(db, record, run, token);
    }

    private async Task ProcessResourcesAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        CollectionMigrationRunRecord record, MigrationRun run, CollectionMigrationCollectionRecord row,
        MigrationCollection collection, CancellationToken token)
    {
        var entries = collection.Resources!;
        if (collection.State is "collecting" or "verifying")
        {
            if (collection.Pages >= 1000 && collection.State == "collecting") throw new InvalidDataException();
            var source = await SourceAsync(execution, await AccountAsync(db, record.OwnerUserId, run.ProviderAccountId, token), token);
            await VerifySourceAsync(source, run, token);
            var verifying = collection.State == "verifying";
            var result = await source.Implementation.GetLibraryResourcesAsync(source.Context, collection.Kind,
                new(100, verifying ? collection.VerifyCursor : collection.Cursor));
            if (!result.IsSuccess) throw new InvalidOperationException();
            var page = result.RequireValue();
            if (collection.Expected != page.Total) throw new InvalidDataException();
            if (verifying)
            {
                if (!entries.Skip(collection.Verified).Take(page.Items.Count).Select(item => JsonSerializer.Serialize(item.Source))
                    .SequenceEqual(page.Items.Select(item => JsonSerializer.Serialize(item)))) throw new InvalidDataException();
                collection.Verified += page.Items.Count;
                collection.VerifyCursor = page.NextCursor;
                if (page.NextCursor == null)
                {
                    if (collection.Verified != collection.Read) throw new InvalidDataException();
                    collection.State = "matching";
                }
            }
            else
            {
                if (page.Items.Select(item => item.SourceId).Distinct().Count() != page.Items.Count ||
                    page.Items.Any(item => entries.Any(saved => saved.Source.SourceId == item.SourceId))) throw new InvalidDataException();
                entries.AddRange(page.Items.Select(item => new MigrationResourceEntry { Id = Guid.CreateVersion7(), Kind = collection.Kind, Source = item }));
                collection.Read = entries.Count;
                collection.Cursor = page.NextCursor;
                collection.Pages++;
                if (page.NextCursor == null) collection.State = "verifying";
            }
        }
        else if (collection.State == "matching")
        {
            foreach (var entry in entries.Skip(collection.Matched).Take(10))
            {
                await MatchResourceAsync(execution, entry, token);
                collection.Matched++;
            }
            if (collection.Matched == collection.Read) collection.State = "review";
        }
        else if (collection.State == "applying")
        {
            var enabled = await catalog.GetPlayableProviderOrderAsync(execution.RequireActor(), token);
            foreach (var entry in entries.Skip(collection.AppliedPosition).Take(50))
            {
                if (entry.State == "accepted" && !entry.Applied)
                {
                    var chosen = entry.Chosen!;
                    if (!enabled.Contains(chosen.ProviderId)) throw new InvalidOperationException();
                    var type = collection.Kind == "saved-albums" ? "album" : "artist";
                    var itemId = $"ext-{chosen.ProviderId}-{type}-{chosen.ResourceId}";
                    var imported = await favorites.ImportMigrationLikesAsync(execution, [(itemId, $"migration:{row.Id:N}:{entry.Id:N}")], run.CreatedAt, token);
                    var hearted = imported[0] || (await new FavoriteViewReader(factory).ReadAsync(execution, token)).Any(item => item.ItemId == itemId);
                    if (hearted && type == "artist" && !(await releases.ListAsync(record.OwnerUserId, token))
                        .Any(item => item.ProviderId == chosen.ProviderId && item.ArtistId == chosen.ResourceId))
                        await releases.FollowAsync(record.OwnerUserId, new(chosen.ProviderId, chosen.ResourceId, chosen.Title), token);
                    entry.Applied = hearted;
                }
                collection.AppliedPosition++;
            }
            if (collection.AppliedPosition == collection.Read) collection.State = "applied";
        }
    }

    private async Task MatchResourceAsync(ProtocolExecutionContext execution, MigrationResourceEntry entry, CancellationToken token)
    {
        var source = entry.Source;
        var album = entry.Kind == "saved-albums";
        var enabled = await catalog.GetPlayableProviderOrderAsync(execution.RequireActor(), token);
        var accepted = new List<(MigrationResourceCandidate Candidate, Album? Album)>();
        entry.Candidates.Clear();
        foreach (var provider in enabled.Where(item => item != "spotify"))
        {
            var found = await catalog.SearchAsync(execution, album ? $"{source.Title} {source.Artists.FirstOrDefault()}" : source.Title, 0, album ? 10 : 0, album ? 0 : 10, provider);
            if (album)
                foreach (var result in found.Albums.Take(10))
                {
                    var id = FavoriteDisplaySnapshot.Resource(result.Id);
                    if (id?.ProviderId != provider || id.ResourceKind != ProviderResourceKind.Album) continue;
                    var detail = await catalog.GetAlbumAsync(execution, provider, id.Value);
                    if (detail == null || detail.Id != result.Id) continue;
                    var title = MusicAlbumReleases.Collapse([detail])[0].Title;
                    if (detail.IsExplicit is { } rating && !title.EndsWith("(Explicit)", StringComparison.OrdinalIgnoreCase) &&
                        !title.EndsWith("(Clean)", StringComparison.OrdinalIgnoreCase)) title += rating ? " (Explicit)" : " (Clean)";
                    var candidate = new MigrationResourceCandidate(provider, id.Value, title, detail.Artist, detail.Year,
                        detail.SongCount, detail.CoverArtUrl, []);
                    entry.Candidates.Add(candidate);
                    if (SameAlbum(source, detail)) accepted.Add((candidate, detail));
                }
            else
                foreach (var result in found.Artists.Take(10))
                {
                    var id = FavoriteDisplaySnapshot.Resource(result.Id);
                    if (id?.ProviderId != provider || id.ResourceKind != ProviderResourceKind.Artist) continue;
                    var albums = await catalog.GetArtistAlbumsAsync(execution, provider, id.Value);
                    var candidate = new MigrationResourceCandidate(provider, id.Value, result.Name, null, null, null,
                        result.ImageUrl, albums.Take(2).Select(item => item.Title).ToArray());
                    entry.Candidates.Add(candidate);
                    var corroborated = Guid.TryParse(source.MusicBrainzArtistId, out var sourceMbid) &&
                        result.ProviderIds.Any(item => (item.Key.Equals("MusicBrainzArtist", StringComparison.OrdinalIgnoreCase) || item.Key.Equals("musicbrainz", StringComparison.OrdinalIgnoreCase)) &&
                            Guid.TryParse(item.Value, out var candidateMbid) && sourceMbid == candidateMbid) ||
                        (source.Albums ?? []).Select(Key).Intersect(albums.Where(item => Key(item.Artist) == Key(source.Title)).Select(item => Key(item.Title))).Any();
                    if (Key(source.Title) == Key(result.Name) && corroborated) accepted.Add((candidate, null));
                }
        }
        var groups = album ? MusicAlbumReleases.Partition(accepted.Select(item => item.Album!)).Count : accepted.Count;
        entry.Chosen = groups == 1 ? accepted[0].Candidate : null;
        entry.State = entry.Chosen != null ? "accepted" : entry.Candidates.Count > 0 ? "review" : "unmatched";
    }

    private static string Key(string value) => FuzzyMatcher.NormalizeForMatching(value);
    private static int? ReleaseYear(string? date) => date?.Length >= 4 && int.TryParse(date[..4], out var year) ? year : null;
    internal static bool SameAlbum(ProviderLibraryResource source, Album candidate)
    {
        var original = new Album
        {
            Title = source.Title,
            Artist = source.Artists.FirstOrDefault() ?? "",
            Year = ReleaseYear(source.ReleaseDate),
            SongCount = source.TrackCount,
            Upc = source.Upc,
            IsExplicit = source.IsExplicit,
            Edition = source.Edition
        };
        static bool? Rating(string title) => title.EndsWith("(Clean)", StringComparison.OrdinalIgnoreCase) ? false
            : title.EndsWith("(Explicit)", StringComparison.OrdinalIgnoreCase) ? true : null;
        original.IsExplicit ??= Rating(original.Title);
        if (MusicAlbumReleases.FamilyKey(original) != MusicAlbumReleases.FamilyKey(candidate) ||
            original.Year == null || original.Year != candidate.Year || original.SongCount is not > 0 || original.SongCount != candidate.SongCount ||
            original.IsExplicit != (candidate.IsExplicit ?? Rating(candidate.Title))) return false;
        var upc = MusicAlbumReleases.NormalizeUpc(source.Upc);
        return string.IsNullOrWhiteSpace(source.Upc) || upc != null && upc == MusicAlbumReleases.NormalizeUpc(candidate.Upc) && MusicAlbumReleases.SameRelease(original, candidate);
    }
}
