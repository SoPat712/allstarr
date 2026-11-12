using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryArtistLink(string ProviderId, string ArtistId);

/// <summary>A person's own answer for one service artist: the same artist as the library one, or not. Household fixes come from administrators.</summary>
public sealed record LibraryArtistFix(string ProviderId, string ArtistId, bool Same, bool Household);

public sealed partial class LibraryArtistIndex(IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraryAccess, IPlatformClock clock, TrackMatchPlayableSearch search,
    IProtocolProviderGateway providers, ITrackIdentityService identities, IProviderRegistry? registry = null,
    EmptyServiceArtists? emptyArtists = null)
{
    private const string IndexKind = "library-artist-index", BackfillKind = "library-artist-backfill", FixKind = "library-artist-fix";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private sealed record Backfill(Guid JobId, Guid[] Tried, int Attempted);

    public async Task<IReadOnlyList<LibraryArtistLink>> ReadAsync(Guid owner, string name, CancellationToken token = default)
    {
        var (songs, fixes) = await ReadPartsAsync(owner, name, token);
        return Apply(songs, fixes);
    }

    /// <summary>The links matched songs prove, and the fixes people made on top of them, kept apart so apps can say which is which.</summary>
    public async Task<(IReadOnlyList<LibraryArtistLink> Songs, IReadOnlyList<LibraryArtistFix> Fixes)> ReadPartsAsync(Guid owner, string name,
        CancellationToken token = default)
    {
        if (!ValidName(name)) return ([], []);
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return ([], []);
        using (RequestTiming.Current?.Stage("lock")) await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var songs = (await ComputedAsync(db, owner, access, name, token)).Where(link => Usable(link.ArtistId)).ToArray();
            var fixes = (await FixesAsync(db, owner, access.Context, [name], token)).GetValueOrDefault(name) ?? [];
            return (songs, fixes);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Saves one person's answer for a service artist; null clears it. An administrator's answer applies to the whole
    /// household, anyone else's only to them, and a person's own answer wins over the household's.
    /// </summary>
    public async Task<bool> SetFixAsync(Guid owner, string name, string providerId, string artistId, bool? same, CancellationToken token = default)
        => await SetFixAsync(owner, name, providerId, artistId, same, null, token);

    public async Task<bool> SetFixAsync(Guid owner, string name, string providerId, string artistId, bool? same, bool? household,
        CancellationToken token = default)
    {
        if (!ValidName(name) || string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(artistId) || artistId.Length > 300) return false;
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return false;
        var administrator = access.Context.Principal?.IsAdministrator == true;
        if (household == true && !administrator) return false;
        var shared = household ?? administrator;
        var provider = NormalizeProvider(providerId);
        var key = shared ? HouseholdKey(access.Context, name) : Key(access.Context, name);
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var previous = shared ? await LatestHouseholdAsync(db, key, token) : await LatestAsync(db, owner, FixKind, key, token);
            var fixes = ParseFixes(previous?.PayloadJson, shared)
                .Where(fix => !(fix.ProviderId == provider && fix.ArtistId == artistId)).ToList();
            if (same is { } value) fixes.Add(new(provider, artistId, value, shared));
            // Versions count per person, so a household row continues this administrator's own numbering.
            var numbered = shared ? await LatestAsync(db, owner, FixKind, key, token) : previous;
            await SaveAsync(db, access.Context, FixKind, key, fixes.ToArray(), "fix", numbered, token);
            return true;
        }
        finally { Gate.Release(); }
    }

    // A "credit:" id is a placeholder for a song credit with no real artist behind it; it can't be opened or matched.
    private static bool Usable(string artistId) => !artistId.StartsWith("credit:", StringComparison.Ordinal);

    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 300 && !name.Any(char.IsControl);

    // Fixes win over matched songs: "not the same" removes a link and "same" adds one.
    internal static IReadOnlyList<LibraryArtistLink> Apply(IReadOnlyList<LibraryArtistLink> songs, IReadOnlyList<LibraryArtistFix> fixes)
    {
        if (fixes.Count == 0) return songs;
        var refused = fixes.Where(fix => !fix.Same).Select(fix => (fix.ProviderId, fix.ArtistId)).ToHashSet();
        return songs.Where(link => !refused.Contains((link.ProviderId, link.ArtistId)))
            .Concat(fixes.Where(fix => fix.Same).Select(fix => new LibraryArtistLink(fix.ProviderId, fix.ArtistId)))
            .Distinct().ToArray();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistFix>>> FixesAsync(AllstarrDbContext db, Guid owner,
        ProtocolExecutionContext context, IReadOnlyCollection<string> names, CancellationToken token)
    {
        var personal = names.ToDictionary(name => name, name => Key(context, name), StringComparer.Ordinal);
        var shared = names.ToDictionary(name => name, name => HouseholdKey(context, name), StringComparer.Ordinal);
        var hashes = personal.Values.Concat(shared.Values).Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.ProviderId == "allstarr" &&
                item.ResourceKind == FixKind && hashes.Contains(item.ExternalIdHash))
            .Select(item => new { item.OwnerUserId, item.ExternalIdHash, item.RetrievedAt, item.SnapshotVersion, item.PayloadJson })
            .ToArrayAsync(token);
        var byHash = rows.ToLookup(row => row.ExternalIdHash, StringComparer.Ordinal);
        string? Latest(string hash, bool household) => byHash[hash].Where(row => household || row.OwnerUserId == owner)
            .OrderByDescending(row => row.RetrievedAt).ThenByDescending(row => row.SnapshotVersion).FirstOrDefault()?.PayloadJson;
        var result = new Dictionary<string, IReadOnlyList<LibraryArtistFix>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var mine = ParseFixes(Latest(personal[name], false), false);
            var house = ParseFixes(Latest(shared[name], true), true)
                .Where(fix => !mine.Any(own => own.ProviderId == fix.ProviderId && own.ArtistId == fix.ArtistId));
            var fixes = mine.Concat(house).ToArray();
            if (fixes.Length > 0) result[name] = fixes;
        }
        return result;
    }

    private static IReadOnlyList<LibraryArtistFix> ParseFixes(string? payload, bool household)
    {
        if (payload == null) return [];
        try { return (JsonSerializer.Deserialize<LibraryArtistFix[]>(payload) ?? []).Select(fix => fix with { Household = household }).ToArray(); }
        catch (JsonException) { return []; }
    }

    private static Task<ExternalMetadataSnapshotRecord?> LatestHouseholdAsync(AllstarrDbContext db, string key, CancellationToken token) =>
        db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.ProviderId == "allstarr" && item.ResourceKind == FixKind &&
            item.ExternalIdHash == key).OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
            .FirstOrDefaultAsync(token);

    private static string HouseholdKey(ProtocolExecutionContext context, string name) => CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
    {
        context.Protocol,
        context.BackendInstanceId,
        Household = true,
        Name = FuzzyMatcher.NormalizeForMatching(name)
    }));

    private async Task<IReadOnlyList<LibraryArtistLink>> ComputedAsync(AllstarrDbContext db, Guid owner, BackendLibraryAccessContext access,
        string name, CancellationToken token, List<LibraryArtistEvidence>? evidence = null)
    {
        var tracks = await TracksAsync(db, access, name, token);
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
        // Callers resolve access first and stop when it has no context.
        var context = access.Context!;
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
            Rule = 4,
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
        if (previous?.ProviderRevision == fingerprint && evidence == null)
            return JsonSerializer.Deserialize<LibraryArtistLink[]>(previous.PayloadJson) ?? [];
        var ids = current.Select(item => item.Id).ToArray();
        var metadata = await snapshots.Where(item => ids.Contains(item.Id)).ToArrayAsync(token);
        var votes = new List<(string Provider, Guid Recording, string[] Artists, bool Named, string Name)>();
        foreach (var link in links)
        {
            foreach (var snapshot in metadata.Where(item => item.ProviderId == link.ProviderId && item.ExternalIdHash == link.ExternalIdHash &&
                (item.ProviderTrackIdentityId == null || item.ProviderTrackIdentityId == link.Id) &&
                (link.Scope == ProviderIdentityScope.Catalog || item.ProviderAccountId == link.ProviderAccountId))
                .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion))
            {
                (string[] Ids, bool Named, string Name) artists;
                try { artists = ArtistIds(snapshot.PayloadJson, link.ProviderId, name, soloRecordings.Contains(link.CanonicalRecordingId)); }
                catch (JsonException) { continue; }
                if (artists.Ids.Length == 0) continue;
                votes.Add((NormalizeProvider(link.ProviderId), link.CanonicalRecordingId, artists.Ids, artists.Named, artists.Name));
                break;
            }
        }
        var result = new List<LibraryArtistLink>();
        foreach (var provider in votes.GroupBy(item => item.Provider))
        {
            // A song linked to several of the service's artists counts against the majority but votes for none.
            var songs = provider.GroupBy(item => item.Recording).Select(group => (
                Recording: group.Key,
                Ids: group.SelectMany(item => item.Artists).Distinct(StringComparer.Ordinal).ToArray(),
                Named: group.Any(item => item.Named))).ToArray();
            if (evidence != null)
                foreach (var artist in provider.SelectMany(vote => vote.Artists).Distinct(StringComparer.Ordinal))
                {
                    var shared = songs.Where(song => song.Ids.Length == 1 && song.Ids[0] == artist).ToArray();
                    evidence.Add(new(provider.Key, artist, provider.First(vote => vote.Artists.Contains(artist)).Name, shared.Length,
                        tracks.Where(track => shared.Any(vote => vote.Recording == track.CanonicalRecordingId))
                            .DistinctBy(track => track.CanonicalRecordingId).Take(5).Select(track => track.Title).ToArray(), shared.Any(vote => vote.Named)));
                }
            var best = songs.Where(song => song.Ids.Length == 1).GroupBy(song => song.Ids[0])
                .OrderByDescending(group => group.Count()).FirstOrDefault();
            if (best == null) continue;
            // One song is enough when the service credits the library's own name; a differently credited artist needs two.
            var needed = best.Any(song => song.Named) ? 1 : 2;
            if (best.Count() >= needed && best.Count() * 2 > songs.Length) result.Add(new(provider.Key, best.Key));
        }
        if (previous?.ProviderRevision != fingerprint)
            await SaveAsync(db, context, IndexKind, key, result.ToArray(), fingerprint, previous, token);
        return result;
    }

    // The last saved links for each name, without rechecking the songs behind them, for hot paths such as search.
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistLink>>> ReadSavedAsync(Guid owner,
        IReadOnlyCollection<string> names, CancellationToken token = default)
    {
        var result = new Dictionary<string, IReadOnlyList<LibraryArtistLink>>(StringComparer.Ordinal);
        var valid = names.Where(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 300 && !name.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (valid.Length == 0) return result;
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return result;
        await using var db = await factory.CreateDbContextAsync(token);
        result = await ReadSavedAsync(db, owner, access.Context, valid, token);
        foreach (var (name, fixes) in await FixesAsync(db, owner, access.Context, valid, token))
            result[name] = Apply(result.GetValueOrDefault(name) ?? [], fixes);
        return result;
    }

    private static async Task<Dictionary<string, IReadOnlyList<LibraryArtistLink>>> ReadSavedAsync(AllstarrDbContext db, Guid owner,
        ProtocolExecutionContext context, IReadOnlyCollection<string> names, CancellationToken token)
    {
        var result = new Dictionary<string, IReadOnlyList<LibraryArtistLink>>(StringComparer.Ordinal);
        var keys = names.ToDictionary(name => name, name => Key(context, name), StringComparer.Ordinal);
        var hashes = keys.Values.Distinct(StringComparer.Ordinal).ToArray();
        var saved = (await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
                item.ProviderId == "allstarr" && item.ResourceKind == IndexKind && hashes.Contains(item.ExternalIdHash))
            .Select(item => new { item.ExternalIdHash, item.SnapshotVersion, item.PayloadJson }).ToArrayAsync(token))
            .GroupBy(item => item.ExternalIdHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.MaxBy(item => item.SnapshotVersion)!.PayloadJson, StringComparer.Ordinal);
        foreach (var (name, key) in keys)
        {
            if (!saved.TryGetValue(key, out var payload)) continue;
            try { result[name] = (JsonSerializer.Deserialize<LibraryArtistLink[]>(payload) ?? []).Where(link => Usable(link.ArtistId)).ToArray(); }
            catch (JsonException) { }
        }
        return result;
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

    private (string[] Ids, bool Named, string Name) ArtistIds(string payload, string provider, string name, bool allowSingleArtist)
    {
        var song = TrackIdentityService.ReadDisplaySong(payload, provider, "stored");
        if (song == null) return ([], false, name);
        var named = song.Artists.Select((artist, index) => (Name: artist, Id: song.ArtistIds.ElementAtOrDefault(index)))
            .Where(item => FuzzyMatcher.NormalizeForMatching(item.Name) == FuzzyMatcher.NormalizeForMatching(name))
            .Select(item => item.Id).Concat(song.AlbumArtists.Where(artist =>
                FuzzyMatcher.NormalizeForMatching(artist.Name) == FuzzyMatcher.NormalizeForMatching(name)).Select(artist => artist.Id))
            .OfType<string>().Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        var ids = named.Length > 0 ? named : song.ArtistIds.Append(song.ArtistId ?? "").Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (named.Length == 0 && (!allowSingleArtist || ids.Length != 1 || song.Artists.Count != 1)) return ([], false, name);
        // A lone credit can carry its id outside the artist list; the name it shows still counts.
        var sameName = named.Length > 0 || song.Artists.Count == 1 &&
            FuzzyMatcher.NormalizeForMatching(song.Artists[0]) == FuzzyMatcher.NormalizeForMatching(name);
        return (ids.Select(ArtistId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), sameName, sameName ? name : song.Artist);

        string? ArtistId(string id)
        {
            if (!id.StartsWith("ext-", StringComparison.Ordinal)) return Usable(id) ? id : null;
            const string marker = "-artist-";
            var at = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at <= 4 || NormalizeProvider(id[4..at]) != NormalizeProvider(provider)) return null;
            var value = id[(at + marker.Length)..];
            return !string.IsNullOrWhiteSpace(value) && Usable(value) ? value : null;
        }
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
