using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientEvents;
using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Releases;

public sealed class NewReleasesService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock,
    IProviderRouter router, IDurableRuntimeSettings settings, IClientEventPublisher? events = null, ILogger<NewReleasesService>? logger = null)
{
    public const string JobType = "releases.poll";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string FollowsKind = "release-follows";
    private const string CatalogKind = "release-catalog";
    private const string RateKind = "release-rate";
    private const string DismissKind = "release-dismissal";
    private const int FollowTopArtists = 25, FollowMinimumPlays = 5;
    private static readonly string[] FollowSources = ["deezer", "apple-musickit", "spotiflac-deezer", "spotiflac-tidal-web", "spotiflac-spotify-web", "spotiflac-apple-music"];

    public async Task<IReadOnlyList<FollowedReleaseArtist>> ListAsync(Guid owner, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        return await FollowsAsync(db, user, token);
    }

    public async Task FollowAsync(Guid owner, FollowedReleaseArtist artist, CancellationToken token = default,
        FollowedReleaseArtist? legacy = null)
    {
        var id = new ProviderExternalResourceId(artist.ProviderId, ProviderResourceKind.Artist, artist.ArtistId);
        if (string.IsNullOrWhiteSpace(artist.DisplayName) || artist.DisplayName.Length > 300 || artist.DisplayName.Any(char.IsControl))
            throw new ArgumentException("An artist display name is required.");
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var before = await ReadForEventAsync(owner, token);
            var artists = (await FollowsAsync(db, user, token)).ToList();
            if (legacy != null)
            {
                var index = artists.FindIndex(item => item.LibraryArtistId == null && item.IsDirectFollow == null &&
                    item.ProviderId == legacy.ProviderId && item.ArtistId == legacy.ArtistId);
                if (index < 0 || artist.LibraryArtistId == null) return;
                artists.RemoveAt(index);
            }
            var followed = artist with
            {
                DisplayName = artist.DisplayName.Trim(),
                MusicBrainzArtistId = artist.LibraryArtistId == null ? await KnownArtistMbidAsync(db, user, id, token) : artist.MusicBrainzArtistId,
                IsDirectFollow = artist.LibraryArtistId == null
            };
            if (artist.LibraryArtistId != null && await HasLibraryHeartAsync(db, user, artist.LibraryArtistId, token))
            {
                var serviceHearts = await db.FavoriteStates.AsNoTracking().Where(item => item.OwnerUserId == owner && item.IsFavorite &&
                    item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId)
                    .Select(item => item.ItemId).ToArrayAsync(token);
                var serviceIds = serviceHearts.Select(FavoriteDisplaySnapshot.Resource).OfType<ProviderExternalResourceId>()
                    .Where(item => item.ResourceKind == ProviderResourceKind.Artist)
                    .Select(item => (Provider: AppleMusicClient.NormalizeProviderId(item.ProviderId), Id: item.Value)).ToHashSet();
                artists.RemoveAll(item => item.LibraryArtistId == null && item.IsDirectFollow == null &&
                    item.DisplayName.Equals(artist.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                    !serviceIds.Contains((AppleMusicClient.NormalizeProviderId(item.ProviderId), item.ArtistId)));
            }
            artists.RemoveAll(item => artist.LibraryArtistId != null ? SameLibraryId(item.LibraryArtistId, artist.LibraryArtistId) :
                item.LibraryArtistId == null && item.ProviderId == id.ProviderId && item.ArtistId == id.Value);
            artists.Add(followed);
            await SaveAsync(db, user, FollowsKind, Scope(user), artists.ToArray(), token);
            await EnsureScheduleAsync(db, user, token);
            await db.SaveChangesAsync(token);
            await PublishInboxChangeAsync(owner, before, token);
        }
        finally { Gate.Release(); }
    }

    // Brings the listener's daily release check forward to now; the schedule engine starts it within a second.
    public async Task<bool> CheckNowAsync(Guid owner, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            if (!(await FollowsAsync(db, user, token)).Any()) return false;
            await EnsureScheduleAsync(db, user, token);
            var id = ScheduleId(user);
            var schedule = db.JobSchedules.Local.SingleOrDefault(item => item.Id == id) ??
                await db.JobSchedules.SingleAsync(item => item.Id == id, token);
            schedule.NextRunAt = clock.UtcNow;
            schedule.Revision++;
            await db.SaveChangesAsync(token);
            return true;
        }
        finally { Gate.Release(); }
    }

    public async Task UnfollowAsync(Guid owner, string provider, string artistId, CancellationToken token = default,
        string? libraryArtistId = null)
    {
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var before = await ReadForEventAsync(owner, token);
            var artists = (await FollowsAsync(db, user, token)).Where(item => libraryArtistId != null
                ? !SameLibraryId(item.LibraryArtistId, libraryArtistId)
                : item.LibraryArtistId != null || item.ProviderId != provider || item.ArtistId != artistId).ToArray();
            await SaveAsync(db, user, FollowsKind, Scope(user), artists, token);
            if (artists.Length == 0)
            {
                var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == ScheduleId(user), token);
                if (schedule != null) { schedule.Enabled = false; schedule.Revision++; schedule.UpdatedAt = clock.UtcNow; }
            }
            await db.SaveChangesAsync(token);
            await PublishInboxChangeAsync(owner, before, token);
        }
        finally { Gate.Release(); }
    }

    // Service artists the listener favourited in any app are followed too; only new follows are added.
    public async Task<int> FollowFavoriteArtistsAsync(Guid owner, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var rows = await db.FavoriteStates.AsNoTracking().Where(state => state.OwnerUserId == owner && state.IsFavorite &&
                state.Protocol == user.BackendType && state.BackendInstanceId == user.BackendInstanceId)
            .Select(state => new { state.ItemId, state.DisplayMetadataJson }).ToArrayAsync(token);
        var artists = rows.Select(row => FavoriteDisplaySnapshot.Read(row.ItemId, row.DisplayMetadataJson))
            .OfType<FavoriteDisplaySnapshot>().Where(item => item.Kind == "artist" && !string.IsNullOrWhiteSpace(item.Title))
            .Select(item => (Id: FavoriteDisplaySnapshot.Resource(item.ItemId), Name: item.Title))
            .Where(item => item.Id?.ResourceKind == ProviderResourceKind.Artist).Take(200).ToArray();
        if (artists.Length == 0) return 0;
        await Gate.WaitAsync(token);
        try
        {
            var follows = (await FollowsAsync(db, user, token)).ToList();
            var added = 0;
            foreach (var (id, name) in artists)
            {
                if (follows.Any(item => item.LibraryArtistId == null && item.ProviderId == id!.ProviderId && item.ArtistId == id.Value)) continue;
                follows.Add(new(id!.ProviderId, id.Value, name, await KnownArtistMbidAsync(db, user, id, token), IsDirectFollow: true));
                added++;
            }
            if (added == 0) return 0;
            var before = await ReadForEventAsync(owner, token);
            await SaveAsync(db, user, FollowsKind, Scope(user), follows.ToArray(), token);
            await EnsureScheduleAsync(db, user, token);
            await db.SaveChangesAsync(token);
            await PublishInboxChangeAsync(owner, before, token);
            return added;
        }
        finally { Gate.Release(); }
    }

    public async Task<int> FollowHeartedArtistsAsync(Guid owner, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var rows = await (from state in db.FavoriteStates.AsNoTracking()
                          join accepted in db.FavoriteEvents on state.LastEventId equals accepted.Id
                          where state.OwnerUserId == owner && state.IsFavorite && state.Protocol == user.BackendType &&
                              state.BackendInstanceId == user.BackendInstanceId && accepted.OwnerUserId == owner &&
                              accepted.Protocol == state.Protocol && accepted.BackendInstanceId == state.BackendInstanceId &&
                              accepted.BackendPrincipalId == user.BackendPrincipalId && accepted.ItemId == state.ItemId &&
                              accepted.Operation == FavoriteOperation.Favorite
                          orderby accepted.CreatedAt descending, state.Id
                          select new { state.ItemId, state.DisplayMetadataJson }).ToArrayAsync(token);
        var artists = rows.Select(row => FavoriteDisplaySnapshot.Read(row.ItemId, row.DisplayMetadataJson))
            .OfType<FavoriteDisplaySnapshot>().Where(item => item.Kind == "song")
            .SelectMany(song => song.ArtistIds.Select((id, index) => (Id: FavoriteDisplaySnapshot.Resource(id),
                Name: song.Artists.ElementAtOrDefault(index))))
            .Where(item => item.Id?.ResourceKind == ProviderResourceKind.Artist && !string.IsNullOrWhiteSpace(item.Name))
            .DistinctBy(item => (item.Id!.ProviderId, item.Id.Value)).Take(200).ToArray();
        await Gate.WaitAsync(token);
        try
        {
            var follows = (await FollowsAsync(db, user, token)).ToList();
            var added = 0;
            foreach (var artist in artists)
            {
                var id = artist.Id!;
                if (follows.Any(item => item.ProviderId == id.ProviderId && item.ArtistId == id.Value)) continue;
                follows.Add(new(id.ProviderId, id.Value, artist.Name!, await KnownArtistMbidAsync(db, user, id, token)));
                added++;
            }
            if (added > 0)
            {
                var before = await ReadForEventAsync(owner, token);
                await SaveAsync(db, user, FollowsKind, Scope(user), follows.ToArray(), token);
                await EnsureScheduleAsync(db, user, token);
                await db.SaveChangesAsync(token);
                await PublishInboxChangeAsync(owner, before, token);
            }
            return added;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Follows the artists a listener plays most, looking each one up by exact name in a source that can list releases.</summary>
    public async Task<int> FollowTopArtistsAsync(Guid owner, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var since = clock.UtcNow.AddYears(-3);
        var names = (await db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == owner && item.Protocol == user.BackendType &&
                item.BackendInstanceId == user.BackendInstanceId && item.State == ListeningEventState.Completed && item.Artist != null && item.ListenedAt >= since)
            .GroupBy(item => item.Artist!).Select(group => new { Name = group.Key, Plays = group.Count() }).ToArrayAsync(token))
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Plays: group.Sum(item => item.Plays)))
            .Where(item => FuzzyMatcher.SplitCredits(item.Name).Count == 1 || item.Plays >= FollowMinimumPlays * 2)
            .Where(item => item.Plays >= FollowMinimumPlays).OrderByDescending(item => item.Plays).ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(FollowTopArtists).Select(item => item.Name).ToArray();
        if (names.Length == 0) return 0;
        var sources = await FollowSourcesAsync(user, token);
        if (sources.Length == 0) return 0;
        using var searches = new SemaphoreSlim(4);
        var found = await Task.WhenAll(names.Select(async name =>
        {
            await searches.WaitAsync(token);
            try
            {
                return await FindArtistByNameAsync(sources, name);
            }
            finally { searches.Release(); }
        }));
        await Gate.WaitAsync(token);
        try
        {
            var follows = (await FollowsAsync(db, user, token)).ToList();
            var added = 0;
            foreach (var artist in found.OfType<FollowedReleaseArtist>())
            {
                if (follows.Any(item => item.ProviderId == artist.ProviderId && item.ArtistId == artist.ArtistId ||
                    item.DisplayName.Equals(artist.DisplayName, StringComparison.OrdinalIgnoreCase))) continue;
                var id = new ProviderExternalResourceId(artist.ProviderId, ProviderResourceKind.Artist, artist.ArtistId);
                follows.Add(artist with { MusicBrainzArtistId = await KnownArtistMbidAsync(db, user, id, token) });
                added++;
            }
            if (added > 0)
            {
                var before = await ReadForEventAsync(owner, token);
                await SaveAsync(db, user, FollowsKind, Scope(user), follows.ToArray(), token);
                await EnsureScheduleAsync(db, user, token);
                await db.SaveChangesAsync(token);
                await PublishInboxChangeAsync(owner, before, token);
            }
            return added;
        }
        finally { Gate.Release(); }
    }

    public async Task<FollowedReleaseArtist?> ResolveLibraryArtistAsync(Guid owner, string name, string? musicBrainzArtistId,
        IReadOnlyCollection<string> albums, IReadOnlyCollection<string> songs, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 300 || name.Any(char.IsControl)) return null;
        var mbid = Guid.TryParse(musicBrainzArtistId, out var parsed) && parsed != Guid.Empty ? parsed.ToString() : null;
        var albumTitles = albums.Select(title => FuzzyMatcher.NormalizeTitleForMatching(title, name)).Where(title => title.Length > 0).ToHashSet();
        var songTitles = songs.Select(title => FuzzyMatcher.NormalizeTitleForMatching(title, name)).Where(title => title.Length > 0).ToHashSet();
        if (mbid == null && albumTitles.Count == 0 && songTitles.Count == 0) return null;
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var matches = new List<(FollowedReleaseArtist Artist, bool Mbid, int Titles)>();
        foreach (var source in await FollowSourcesAsync(user, token))
        {
            var outcome = await source.Implementation.SearchArtistsAsync(source.Context, new(name.Trim(), new(8)));
            if (!outcome.IsSuccess) continue;
            foreach (var candidate in outcome.RequireValue().Items.DistinctBy(item => item.Id).Take(8))
            {
                var candidateMbid = ArtistMbid(candidate.ProviderIds) ?? await KnownArtistMbidAsync(db, user, candidate.Id, token);
                if (mbid != null && candidateMbid == null)
                {
                    var detail = await source.Implementation.GetArtistAsync(source.Context, new(candidate.Id));
                    if (detail.IsSuccess && detail.RequireValue().Id == candidate.Id) candidateMbid = ArtistMbid(detail.RequireValue().ProviderIds);
                }
                if (mbid != null && candidateMbid != null && mbid != candidateMbid) continue;
                var evidence = 0;
                if (albumTitles.Count > 0)
                {
                    var catalog = await source.Implementation.GetArtistAlbumsAsync(source.Context, new(candidate.Id, new(100)));
                    if (catalog.IsSuccess) evidence += catalog.RequireValue().Items.Select(item => FuzzyMatcher.NormalizeTitleForMatching(item.Title, name))
                        .Distinct().Count(albumTitles.Contains);
                }
                if (songTitles.Count > 0)
                {
                    var tracks = await source.Implementation.GetArtistTracksAsync(source.Context, new(candidate.Id, new(50)));
                    if (tracks.IsSuccess) evidence += tracks.RequireValue().Items.Select(item => FuzzyMatcher.NormalizeTitleForMatching(item.Title, name))
                        .Distinct().Count(songTitles.Contains);
                }
                var matchedMbid = mbid != null && mbid == candidateMbid;
                if (matchedMbid || evidence > 0) matches.Add((new(candidate.Id.ProviderId, candidate.Id.Value, candidate.Name, candidateMbid), matchedMbid, evidence));
            }
        }
        var best = matches.OrderByDescending(item => item.Mbid).ThenByDescending(item => item.Titles)
            .DistinctBy(item => (item.Artist.ProviderId, item.Artist.ArtistId)).ToArray();
        return best.Length == 0 || best.Length > 1 && best[0].Mbid == best[1].Mbid && best[0].Titles == best[1].Titles ? null : best[0].Artist;
    }

    private static async Task<FollowedReleaseArtist?> FindArtistByNameAsync(
        ProviderRouteCandidate<IProviderMetadataCapability>[] sources, string name)
    {
        foreach (var source in sources)
        {
            var outcome = await source.Implementation.SearchArtistsAsync(source.Context, new(name, new(5)));
            if (outcome.IsSuccess && outcome.RequireValue().Items.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } artist)
                return new(artist.Id.ProviderId, artist.Id.Value, artist.Name);
        }
        return null;
    }

    /// <summary>Finds artists by name in the sources that can list their releases, for a listener choosing whom to follow.</summary>
    public async Task<IReadOnlyList<FollowedReleaseArtist>> SearchArtistsAsync(Guid owner, string query, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new ArgumentException("A search is required.");
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        foreach (var source in await FollowSourcesAsync(user, token))
        {
            var outcome = await source.Implementation.SearchArtistsAsync(source.Context, new(query.Trim(), new(8)));
            if (outcome.IsSuccess && outcome.RequireValue().Items.Count > 0)
                return outcome.RequireValue().Items.Select(item => new FollowedReleaseArtist(item.Id.ProviderId, item.Id.Value, item.Name)).ToArray();
        }
        return [];
    }

    private async Task<ProviderRouteCandidate<IProviderMetadataCapability>[]> FollowSourcesAsync(UserRecord user, CancellationToken token)
    {
        var principal = new ProviderBackendPrincipal(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId);
        var policy = new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
            ProviderExplicitContentPolicy.Allow, false, true, false, FollowSources);
        var plan = await router.PlanAsync<IProviderMetadataCapability>(new(ProviderCapabilityKind.Metadata, new(ProviderActorKind.User, user.Id, principal), policy,
            "releases-follow", "releases", DateTimeOffset.UtcNow.AddSeconds(60), FollowSources, cancellationToken: token));
        return plan.Candidates.Where(item => item.Descriptor.Hooks.Contains("getArtistAlbums", StringComparer.Ordinal))
            .OrderBy(item => Array.IndexOf(FollowSources, item.Provider.Id)).ToArray();
    }

    public async Task<ReleaseInbox> ReadAsync(Guid owner, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var sources = new List<ReleaseSource>();
        var coverage = new List<ReleaseCoverage>();
        foreach (var artist in await FollowsAsync(db, user, token))
        {
            if (artist.LibraryArtistId != null && artist.ProviderId == "jellyfin")
            {
                coverage.Add(new(artist, "library-only", null));
                continue;
            }
            var route = await RouteAsync(user, artist, token);
            var cache = route == null ? null : await LatestAsync(db, CatalogKind, CatalogKey(artist, route.Context), token);
            var catalog = Read<ReleaseCatalog>(cache);
            if (catalog != null) sources.AddRange(catalog.Releases);
            coverage.Add(new(artist, catalog == null || catalog.Status == "retry" || catalog.NextCheckAt <= clock.UtcNow && catalog.Cursor == null
                ? "unknown" : catalog.Status, catalog?.CheckedAt));
        }
        var dismissed = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.ResourceKind == DismissKind && item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId &&
            item.BackendPrincipalId == user.BackendPrincipalId).Select(item => item.PayloadJson).ToArrayAsync(token);
        var dismissedSources = dismissed.SelectMany(json => JsonSerializer.Deserialize<ReleaseSource[]>(json) ?? []).ToArray();
        var cleanOnly = (await settings.GetPreferencesAsync(owner, token)).Values.ExplicitFilter == "CleanOnly";
        var entries = ReleaseInboxProjection.Group(sources, clock.UtcNow)
            .Where(item => (!cleanOnly || item.IsExplicit == false) &&
                !item.Sources.Any(source => dismissedSources.Any(dismissed => ReleaseInboxProjection.Dismissed(source, dismissed)))).ToArray();
        return new(entries.Where(item => item.ExclusionReason == null).ToArray(),
            entries.Where(item => item.ExclusionReason != null).ToArray(), coverage);
    }

    public async Task<bool> DismissAsync(Guid owner, string entryId, CancellationToken token = default)
    {
        var inbox = await ReadAsync(owner, token);
        var entry = inbox.Entries.Concat(inbox.Excluded).SingleOrDefault(item => item.Id == entryId);
        if (entry == null) return false;
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            await SaveAsync(db, user, DismissKind, Hash(Scope(user) + entryId), entry.Sources.ToArray(), token);
            await db.SaveChangesAsync(token);
            await PublishInboxChangeAsync(owner, inbox, token);
            return true;
        }
        finally { Gate.Release(); }
    }

    public async Task<DurableJobCompletion> PollAsync(Guid owner, Guid jobId, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            TimeSpan? pending = null;
            void WaitFor(TimeSpan delay) => pending = pending == null || delay < pending.Value ? delay : pending;
            foreach (var artist in await FollowsAsync(db, user, token))
            {
                var route = await RouteAsync(user, artist, token, jobId, WaitFor);
                if (route == null) continue;
                var context = route.Context;
                var key = CatalogKey(artist, context);
                var catalog = Read<ReleaseCatalog>(await LatestAsync(db, CatalogKind, key, token));
                if (catalog != null && catalog.NextCheckAt > clock.UtcNow)
                {
                    if (catalog.Status == "retry") WaitFor(catalog.NextCheckAt - clock.UtcNow);
                    continue;
                }
                var rateKey = Hash(context.ProviderId + ":" + context.Account?.AccountId);
                var rate = Read<ReleaseRate>(await LatestAsync(db, RateKind, rateKey, token));
                if (rate?.NextRequestAt > clock.UtcNow)
                {
                    WaitFor(rate.NextRequestAt - clock.UtcNow);
                    continue;
                }
                await SaveAsync(db, user, RateKind, rateKey, new ReleaseRate(clock.UtcNow.AddSeconds(2)), token);
                await db.SaveChangesAsync(token);
                var cursor = catalog?.Cursor;
                var outcome = await route.Implementation.GetArtistAlbumsAsync(context,
                    new(new(context.ProviderId, ProviderResourceKind.Artist, artist.ArtistId), new(200, cursor)));
                var now = clock.UtcNow;
                if (!outcome.IsSuccess)
                {
                    var error = outcome.Error!;
                    if (error.Kind == ProviderErrorKind.Canceled) token.ThrowIfCancellationRequested();
                    var retry = error.Kind is ProviderErrorKind.RateLimited or ProviderErrorKind.TransientFailure or ProviderErrorKind.Canceled;
                    var delay = error.RetryAfter ?? TimeSpan.FromMinutes(15);
                    if (delay < TimeSpan.FromSeconds(2)) delay = TimeSpan.FromSeconds(2);
                    await SaveAsync(db, user, CatalogKind, key, new ReleaseCatalog(retry ? "retry" : "unknown", now,
                        retry ? now + delay : NextDay(now), catalog?.Releases ?? [], cursor, catalog?.SeenCursors), token);
                    if (retry) await SaveAsync(db, user, RateKind, rateKey, new ReleaseRate(now + delay), token);
                    await db.SaveChangesAsync(token);
                    if (retry) WaitFor(delay);
                    continue;
                }
                var page = outcome.RequireValue();
                var gathered = (cursor == null ? [] : catalog?.Releases ?? []).Concat(page.Items
                    .Where(album => album.Id.ProviderId == context.ProviderId)
                    .Select(album => ReleaseInboxProjection.From(album, artist))).DistinctBy(item => item.Key).ToArray();
                var releases = ReleaseInboxProjection.Group(gathered, now).SelectMany(item => item.Sources).ToArray();
                var seen = cursor == null ? [] : catalog?.SeenCursors?.ToList() ?? [];
                var next = page.NextCursor;
                var stopped = next != null && (seen.Contains(next, StringComparer.Ordinal) || seen.Count >= 19);
                if (next != null) seen.Add(next);
                var before = await CatalogInboxesAsync(db, artist, key, token);
                await SaveAsync(db, user, CatalogKind, key, new ReleaseCatalog(next != null || stopped || page.IsPartial
                    ? "partial" : gathered.Length == 0 ? "unknown" : "known", now,
                    next == null || stopped ? NextDay(now) : now, releases, stopped ? null : next, seen.ToArray()), token);
                await db.SaveChangesAsync(token);
                foreach (var inbox in before) await PublishInboxChangeAsync(inbox.Key, inbox.Value, token);
                if (next != null && !stopped) WaitFor(TimeSpan.FromSeconds(2));
            }
            return pending.HasValue ? Deferred(pending.Value) : DurableJobCompletion.Success();
        }
        finally { Gate.Release(); }
    }

    private async Task<ReleaseInbox?> ReadForEventAsync(Guid owner, CancellationToken token)
    {
        if (events == null) return null;
        try { return await ReadAsync(owner, token); }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Could not read a release inbox for a client update");
            return null;
        }
    }

    private async Task PublishInboxChangeAsync(Guid owner, ReleaseInbox? before, CancellationToken token)
    {
        if (events == null || before == null) return;
        var after = await ReadForEventAsync(owner, token);
        if (after == null) return;
        if (JsonSerializer.Serialize(new { before.Entries, before.Excluded }) !=
            JsonSerializer.Serialize(new { after.Entries, after.Excluded }))
            events.Publish(owner, ClientEvent.Releases(after.Entries.Count));
    }

    private async Task<Dictionary<Guid, ReleaseInbox>> CatalogInboxesAsync(AllstarrDbContext db,
        FollowedReleaseArtist artist, string key, CancellationToken token)
    {
        try { return await ReadCatalogInboxesAsync(db, artist, key, token); }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Could not read release followers for a client update");
            return [];
        }
    }

    private async Task<Dictionary<Guid, ReleaseInbox>> ReadCatalogInboxesAsync(AllstarrDbContext db,
        FollowedReleaseArtist artist, string key, CancellationToken token)
    {
        var result = new Dictionary<Guid, ReleaseInbox>();
        if (events == null) return result;
        var owners = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.ResourceKind == FollowsKind)
            .Select(item => item.OwnerUserId).Distinct().ToArrayAsync(token);
        var users = await db.Users.AsNoTracking().Where(item => item.Enabled && owners.Contains(item.Id)).ToArrayAsync(token);
        foreach (var user in users)
        {
            var followed = (await FollowsAsync(db, user, token)).FirstOrDefault(item =>
                item.ProviderId == artist.ProviderId && item.ArtistId == artist.ArtistId);
            if (followed == null) continue;
            var route = await RouteAsync(user, followed, token);
            if (route != null && CatalogKey(followed, route.Context) == key)
                result[user.Id] = await ReadAsync(user.Id, token);
        }
        return result;
    }

    private async Task<ProviderRouteCandidate<IProviderMetadataCapability>?> RouteAsync(UserRecord user,
        FollowedReleaseArtist artist, CancellationToken token, Guid? jobId = null, Action<TimeSpan>? waitFor = null)
    {
        if (artist.LibraryArtistId != null && artist.ProviderId == "jellyfin") return null;
        var principal = new ProviderBackendPrincipal(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId);
        var actor = jobId.HasValue ? new ProviderActorContext(ProviderActorKind.SystemJob, user.Id, principal, jobId) :
            new ProviderActorContext(ProviderActorKind.User, user.Id, principal);
        var policy = new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
            ProviderExplicitContentPolicy.Allow, false, true, false, [artist.ProviderId]);
        var plan = await router.PlanAsync<IProviderMetadataCapability>(new(ProviderCapabilityKind.Metadata, actor, policy,
            "releases-artist-albums", "releases", DateTimeOffset.UtcNow.AddSeconds(30), [artist.ProviderId], cancellationToken: token));
        var route = plan.Candidates.FirstOrDefault(item => item.Descriptor.Hooks.Contains("getArtistAlbums", StringComparer.Ordinal));
        // A temporary routing block must not finish a poll with artists still unchecked.
        if (route == null && plan.Decision.Candidates.Any(item => item.ReasonCode is
            "circuit-open" or "health-degraded" or "health-unavailable" or "sidecar-not-ready" or "rate-limit-budget-exhausted"))
            waitFor?.Invoke(TimeSpan.FromMinutes(1));
        return route;
    }

    private async Task EnsureScheduleAsync(AllstarrDbContext db, UserRecord user, CancellationToken token)
    {
        var id = ScheduleId(user);
        var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == id, token);
        if (schedule == null)
        {
            schedule = new()
            {
                Id = id,
                OwnerUserId = user.Id,
                JobType = JobType,
                CronExpression = "0 4 * * *",
                TimeZoneId = "UTC",
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                CreatedAt = clock.UtcNow,
                Revision = 1
            };
            db.JobSchedules.Add(schedule);
        }
        if (!schedule.Enabled) { schedule.Enabled = true; schedule.NextRunAt = clock.UtcNow; }
        schedule.UpdatedAt = clock.UtcNow;
    }

    private static Guid ScheduleId(UserRecord user) => new(Convert.FromHexString(Hash("release-schedule:" + Scope(user)))[..16]);
    private static string Scope(UserRecord user) => Hash(user.Id + ":" + user.BackendType + ":" + user.BackendInstanceId + ":" + user.BackendPrincipalId);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CatalogKey(FollowedReleaseArtist artist, ProviderExecutionContext context) =>
        Hash(context.ProviderId + ":" + artist.ArtistId + ":" + context.Account?.AccountId + ":" + context.Account?.Revision);
    private static DateTimeOffset NextDay(DateTimeOffset now) => new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
    private static DurableJobCompletion Deferred(TimeSpan delay) => DurableJobCompletion.Defer("releases-backoff", "Artist catalog polling will continue later.", delay);
    private static T? Read<T>(ExternalMetadataSnapshotRecord? row) => row == null ? default : JsonSerializer.Deserialize<T>(row.PayloadJson);
    private static async Task<UserRecord> UserAsync(AllstarrDbContext db, Guid owner, CancellationToken token) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled, token)
            ?? throw new UnauthorizedAccessException("A linked listener is required.");
    private static Task<ExternalMetadataSnapshotRecord?> LatestAsync(AllstarrDbContext db, string kind, string key, CancellationToken token) =>
        db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.ResourceKind == kind && item.ExternalIdHash == key)
            .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion).ThenByDescending(item => item.Id).FirstOrDefaultAsync(token);
    private static async Task<FollowedReleaseArtist[]> FollowsAsync(AllstarrDbContext db, UserRecord user, CancellationToken token) =>
        Read<FollowedReleaseArtist[]>(await LatestAsync(db, FollowsKind, Scope(user), token)) ?? [];

    private async Task SaveAsync<T>(AllstarrDbContext db, UserRecord user, string kind, string key, T value, CancellationToken token)
    {
        var previous = await LatestAsync(db, kind, key, token);
        var json = JsonSerializer.Serialize(value);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user.Id,
            Protocol = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = user.BackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = kind,
            ExternalIdHash = key,
            SnapshotVersion = (previous?.SnapshotVersion ?? 0) + 1,
            ProviderRevision = "1",
            PayloadJson = json,
            PayloadSha256 = Hash(json),
            CorrelationId = "releases",
            RetrievedAt = clock.UtcNow
        });
    }

    private static async Task<string?> KnownArtistMbidAsync(AllstarrDbContext db, UserRecord user,
        ProviderExternalResourceId artist, CancellationToken token)
    {
        var row = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user.Id &&
            item.ProviderId == artist.ProviderId && item.ResourceKind == "artist" && item.ExternalIdHash == Hash(artist.Value) &&
            item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId && item.BackendPrincipalId == user.BackendPrincipalId)
            .OrderByDescending(item => item.RetrievedAt).FirstOrDefaultAsync(token);
        if (row == null) return null;
        using var payload = JsonDocument.Parse(row.PayloadJson);
        if (!payload.RootElement.TryGetProperty("ProviderIds", out var ids)) return null;
        return ArtistMbid(ids.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(item => item.Name, item => item.Value.GetString()!));
    }

    private static string? ArtistMbid(IReadOnlyDictionary<string, string> ids) => ids
        .Where(item => item.Key.Equals("musicbrainzartist", StringComparison.OrdinalIgnoreCase) || item.Key.Equals("musicbrainz", StringComparison.OrdinalIgnoreCase))
        .Select(item => Guid.TryParse(item.Value, out var mbid) && mbid != Guid.Empty ? mbid.ToString() : null).FirstOrDefault(item => item != null);

    private static bool SameLibraryId(string? first, string second) => first == second ||
        Guid.TryParse(first, out var firstId) && Guid.TryParse(second, out var secondId) && firstId == secondId;

    private static async Task<bool> HasLibraryHeartAsync(AllstarrDbContext db, UserRecord user, string nativeId, CancellationToken token)
    {
        var ids = Guid.TryParse(nativeId, out var guid) ? new[] { nativeId, guid.ToString("N"), guid.ToString("D") } : [nativeId];
        return await (from state in db.FavoriteStates.AsNoTracking()
                      join accepted in db.FavoriteEvents on state.LastEventId equals accepted.Id
                      where state.OwnerUserId == user.Id && state.IsFavorite && ids.Contains(state.ItemId) &&
                          state.Protocol == user.BackendType && state.BackendInstanceId == user.BackendInstanceId &&
                          accepted.OwnerUserId == user.Id && accepted.BackendPrincipalId == user.BackendPrincipalId &&
                          accepted.Protocol == user.BackendType && accepted.BackendInstanceId == user.BackendInstanceId &&
                          accepted.ItemId == state.ItemId && accepted.Operation == FavoriteOperation.Favorite
                      select state.Id).AnyAsync(token);
    }
}

public sealed class NewReleasesJobHandler(NewReleasesService releases) : IDurableJobHandler
{
    public string JobType => NewReleasesService.JobType;
    public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken) =>
        context.Claim.OwnerUserId is { } owner ? releases.PollAsync(owner, context.Claim.JobId, cancellationToken) :
            Task.FromResult(DurableJobCompletion.Failure("releases-owner-missing", "A linked listener is required."));
}
