using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryArtistLink(string ProviderId, string ArtistId);

public sealed class LibraryArtistIndex(IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraryAccess, IPlatformClock clock, TrackMatchPlayableSearch search,
    IProtocolProviderGateway providers, ITrackIdentityService identities, IProviderRegistry? registry = null)
{
    private const string IndexKind = "library-artist-index", BackfillKind = "library-artist-backfill";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private sealed record Backfill(Guid JobId, Guid[] Tried, int Attempted);

    public async Task<IReadOnlyList<LibraryArtistLink>> ReadAsync(Guid owner, string name, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 300 || name.Any(char.IsControl)) return [];
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return [];
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var tracks = await TracksAsync(db, access, name, token);
            if (tracks.Length == 0) return [];
            var recordings = tracks.Select(item => item.CanonicalRecordingId).OfType<Guid>().Distinct().ToArray();
            var soloRecordings = tracks.Where(item => FuzzyMatcher.NormalizeForMatching(item.Artist) == FuzzyMatcher.NormalizeForMatching(name))
                .Select(item => item.CanonicalRecordingId).OfType<Guid>().ToHashSet();
            var links = (await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                recordings.Contains(item.CanonicalRecordingId) && item.ResourceKind == ProviderResourceKind.Track &&
                (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account =>
                    account.Id == item.ProviderAccountId && account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == owner))))
                .ToArrayAsync(token)).Where(CatalogIdentityPolicy.IsPlaybackEligible).OrderBy(item => item.Id).ToArray();
            var providerIds = links.Select(item => item.ProviderId).Distinct().ToArray();
            var hashes = links.Select(item => item.ExternalIdHash).Distinct().ToArray();
            var publicLinks = links.Where(item => item.Scope == ProviderIdentityScope.Catalog).Select(item => item.Id).ToArray();
            var context = access.Context;
            var protocol = context.Protocol.ToString().ToLowerInvariant();
            var snapshots = db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
                (item.OwnerUserId == owner && item.Protocol == protocol && item.BackendInstanceId == context.BackendInstanceId &&
                    item.BackendPrincipalId == context.VerifiedBackendPrincipalId || item.ProviderAccountId == null &&
                    item.ProviderTrackIdentityId != null && publicLinks.Contains(item.ProviderTrackIdentityId.Value)) &&
                providerIds.Contains(item.ProviderId) && hashes.Contains(item.ExternalIdHash) &&
                (item.ResourceKind == "track" || item.ResourceKind == "playback-metadata") &&
                (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                    account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == owner))));
            var stamps = await snapshots.Select(item => new
            {
                item.Id,
                item.ProviderId,
                item.ExternalIdHash,
                item.ProviderAccountId,
                item.ProviderTrackIdentityId,
                item.ResourceKind,
                item.RetrievedAt,
                item.SnapshotVersion
            }).ToArrayAsync(token);
            var current = stamps.GroupBy(item => (item.ProviderId, item.ExternalIdHash, item.ProviderAccountId, item.ResourceKind))
                .Select(group => group.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion).First())
                .OrderBy(item => item.Id).ToArray();
            var fingerprint = CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
            {
                Tracks = tracks.Select(item => new { item.Id, item.CanonicalRecordingId, item.Artist, item.AlbumArtist, item.Revision }),
                Links = links.Select(item => new
                {
                    item.Id,
                    item.CanonicalRecordingId,
                    item.ProviderAccountId,
                    item.Revision,
                    item.ProviderId,
                    item.ExternalIdHash,
                    item.Scope,
                    item.Verification,
                    item.VerificationMethod
                }),
                Snapshots = current.Select(item => item.Id)
            }));
            var key = Key(context, name);
            var previous = await LatestAsync(db, owner, IndexKind, key, token);
            if (previous?.ProviderRevision == fingerprint)
                return JsonSerializer.Deserialize<LibraryArtistLink[]>(previous.PayloadJson) ?? [];
            var ids = current.Select(item => item.Id).ToArray();
            var metadata = await snapshots.Where(item => ids.Contains(item.Id)).ToArrayAsync(token);
            var votes = new List<(string Provider, Guid Recording, string[] Artists)>();
            foreach (var link in links)
            {
                foreach (var snapshot in metadata.Where(item => item.ProviderId == link.ProviderId && item.ExternalIdHash == link.ExternalIdHash &&
                    (item.ProviderTrackIdentityId == null || item.ProviderTrackIdentityId == link.Id) &&
                    (link.Scope == ProviderIdentityScope.Catalog || item.ProviderAccountId == link.ProviderAccountId))
                    .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion))
                {
                    string[] artists;
                    try { artists = ArtistIds(snapshot.PayloadJson, link.ProviderId, name, soloRecordings.Contains(link.CanonicalRecordingId)); }
                    catch (JsonException) { continue; }
                    if (artists.Length == 0) continue;
                    votes.Add((NormalizeProvider(link.ProviderId), link.CanonicalRecordingId, artists));
                    break;
                }
            }
            var result = new List<LibraryArtistLink>();
            foreach (var provider in votes.GroupBy(item => item.Provider))
            {
                var songs = provider.GroupBy(item => item.Recording)
                    .Select(group => group.SelectMany(item => item.Artists).Distinct(StringComparer.Ordinal).ToArray()).ToArray();
                if (songs.Any(song => song.Length != 1)) continue;
                var best = songs.Select(song => song[0]).GroupBy(id => id).OrderByDescending(group => group.Count()).First();
                if (best.Count() >= 2 && best.Count() * 2 > songs.Length) result.Add(new(provider.Key, best.Key));
            }
            await SaveAsync(db, context, IndexKind, key, result.ToArray(), fingerprint, previous, token);
            return result;
        }
        finally { Gate.Release(); }
    }

    // The daily release job visits followed artists first. Continuations share the five-song allowance.
    public async Task<int> BackfillAsync(Guid owner, string name, Guid jobId, CancellationToken token)
    {
        if ((await ReadAsync(owner, name, token)).Count > 0) return 0;
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return 0;
        await using var db = await factory.CreateDbContextAsync(token);
        var context = new ProtocolExecutionContext(access.Context.Protocol, access.Context.BackendInstanceId,
            access.Context.VerifiedBackendPrincipalId, access.Context.Principal, "library-artist-link", clock.UtcNow.AddMinutes(10), token);
        var key = Key(context, name);
        var previous = await LatestAsync(db, owner, BackfillKind, key, token);
        var state = previous == null ? new Backfill(jobId, [], 0) : JsonSerializer.Deserialize<Backfill>(previous.PayloadJson)!;
        var tracks = await TracksAsync(db, access, name, token);
        var tried = state.Tried.ToHashSet();
        var attempted = state.JobId == jobId ? state.Attempted : 0;
        if (state.JobId != jobId && tracks.All(item => tried.Contains(item.Id))) tried.Clear();
        var selected = tracks.Where(item => !tried.Contains(item.Id)).DistinctBy(item => item.CanonicalRecordingId ?? item.Id)
            .Take(Math.Max(0, 5 - attempted)).ToArray();
        using var traffic = new HistoryRequestBudget();
        var failures = new Dictionary<string, int>();
        var count = 0;
        foreach (var track in selected)
        {
            tried.Add(track.Id);
            await SaveAsync(db, context, BackfillKind, key, new Backfill(jobId, tried.ToArray(), ++attempted), "1", previous, token);
            previous = await LatestAsync(db, owner, BackfillKind, key, token);
            count++;
            var isExplicit = track.CanonicalRecordingId is { } canonical ? await db.CanonicalRecordings.AsNoTracking()
                .Where(item => item.Id == canonical).Select(item => item.IsExplicit).SingleOrDefaultAsync(token) : null;
            var source = new ExternalTrackMatchSnapshot(track.Id.ToString(), "allstarr", track.BackendItemId,
                track.Title, track.Artist, track.Album, track.AlbumArtist, track.DurationMilliseconds,
                track.Isrc, track.MusicBrainzRecordingId, isExplicit, track.CanonicalRecordingId);
            var scope = new TrackMatchScope(owner, context.BackendInstanceId, null, 1, 1, access.Access.LibraryIds.ToHashSet());
            var outcome = await search.MatchImportedHistoryAsync(context, source, scope, token, failures);
            if (!outcome.IsSuccess) break;
            var match = outcome.RequireValue();
            if (match.Decision.State != TrackMatchReviewState.Accepted || match.Decision.RequiresReview || match.SelectedExternal is not { } song) continue;
            await providers.RememberTrackAsync(context, $"ext-{song.ExternalProvider}-song-{song.ExternalId}");
            await identities.LinkLibraryRecordingsAsync(context.RequireActor(), context.CorrelationId,
                libraryTrackId: track.Id, cancellationToken: token);
            if ((await ReadAsync(owner, name, token)).Count > 0) break;
        }
        return count;
    }

    private static async Task<LibraryTrackRecord[]> TracksAsync(AllstarrDbContext db, BackendLibraryAccessContext access, string name, CancellationToken token)
    {
        var trimmed = name.Trim();
        var lower = trimmed.ToLowerInvariant();
        return (await LibraryTrackAccess.Query(db, access).Where(item => item.Artist.Contains(trimmed) || item.Artist.ToLower().Contains(lower) ||
                item.AlbumArtist != null && (item.AlbumArtist.Contains(trimmed) || item.AlbumArtist.ToLower().Contains(lower)))
            .OrderBy(item => item.Id).ToArrayAsync(token))
            .Where(item => new[] { item.Artist, item.AlbumArtist ?? "" }.Concat(FuzzyMatcher.SplitCredits(item.Artist))
                .Concat(FuzzyMatcher.SplitCredits(item.AlbumArtist ?? ""))
                .Any(credit => FuzzyMatcher.NormalizeForMatching(credit) == FuzzyMatcher.NormalizeForMatching(name))).ToArray();
    }

    private string[] ArtistIds(string payload, string provider, string name, bool allowSingleArtist)
    {
        var song = TrackIdentityService.ReadDisplaySong(payload, provider, "stored");
        if (song == null) return [];
        var named = song.Artists.Select((artist, index) => (Name: artist, Id: song.ArtistIds.ElementAtOrDefault(index)))
            .Where(item => FuzzyMatcher.NormalizeForMatching(item.Name) == FuzzyMatcher.NormalizeForMatching(name))
            .Select(item => item.Id).Concat(song.AlbumArtists.Where(artist =>
                FuzzyMatcher.NormalizeForMatching(artist.Name) == FuzzyMatcher.NormalizeForMatching(name)).Select(artist => artist.Id))
            .OfType<string>().Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        var ids = named.Length > 0 ? named : song.ArtistIds.Append(song.ArtistId ?? "").Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (named.Length == 0 && (!allowSingleArtist || ids.Length != 1 || song.Artists.Count != 1)) return [];
        return ids.Select(id => LocalLibraryService.ParseExternalResource(id.StartsWith("ext-", StringComparison.Ordinal) ? id : $"ext-{provider}-artist-{id}"))
            .Where(id => id.isExternal && id.type == "artist" && NormalizeProvider(id.provider!) == NormalizeProvider(provider))
            .Select(id => id.externalId!).Distinct(StringComparer.Ordinal).ToArray();
    }

    private string NormalizeProvider(string provider) => registry?.ResolveProviderId(AppleMusicClient.NormalizeProviderId(provider)) ??
        AppleMusicClient.NormalizeProviderId(provider);

    private static string Key(ProtocolExecutionContext context, string name) => CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
    {
        context.Protocol,
        context.BackendInstanceId,
        context.VerifiedBackendPrincipalId,
        Name = FuzzyMatcher.NormalizeForMatching(name)
    }));

    private static Task<ExternalMetadataSnapshotRecord?> LatestAsync(AllstarrDbContext db, Guid owner, string kind, string key, CancellationToken token) =>
        db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner && item.ProviderId == "allstarr" &&
            item.ResourceKind == kind && item.ExternalIdHash == key).OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);

    private async Task SaveAsync<T>(AllstarrDbContext db, ProtocolExecutionContext context, string kind, string key, T payload,
        string revision, ExternalMetadataSnapshotRecord? previous, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(payload);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = context.RequireActor().EffectiveUserId!.Value,
            Protocol = context.Protocol.ToString().ToLowerInvariant(),
            BackendInstanceId = context.BackendInstanceId,
            BackendPrincipalId = context.VerifiedBackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = kind,
            ExternalIdHash = key,
            SnapshotVersion = (previous?.SnapshotVersion ?? 0) + 1,
            ProviderRevision = revision,
            PayloadJson = json,
            PayloadSha256 = CanonicalCatalogKeys.Hash(json),
            CorrelationId = context.CorrelationId,
            RetrievedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(token);
    }
}
