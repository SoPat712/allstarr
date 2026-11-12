using System.Globalization;
using allstarr.Core.Jobs;
using allstarr.Core.ClientEvents;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    internal static bool IsExternalLink(PlaylistLinkRecord link) => link.ImportMode == PlaylistImportMode.Linked &&
        (link.SourceProviderId == "listenbrainz" && (link.SourcePlaylistId.StartsWith("createdfor:", StringComparison.Ordinal) ||
            link.SourcePlaylistId is Providers.ListenBrainz.ListenBrainzPlaylistCapabilityAdapter.Recommended
                or Providers.ListenBrainz.ListenBrainzPlaylistCapabilityAdapter.FreshReleases) ||
         link.SourceProviderId == "lastfm" && (link.SourcePlaylistId is "loved" or "similar" ||
             link.SourcePlaylistId.StartsWith("top:", StringComparison.Ordinal)));

    private static IQueryable<PlaylistLinkRecord> ExternalLinks(AllstarrDbContext db, UserRecord user, bool availableOnly = true) =>
        db.PlaylistLinks.Where(link => link.ImportMode == PlaylistImportMode.Linked && link.OwnerUserId == user.Id && link.TargetProtocol == user.BackendType &&
            !db.ProviderAccounts.Any(account => account.Id == link.ProviderAccountId &&
                (account.OwnerUserId != user.Id || account.ProviderId != link.SourceProviderId)) &&
            link.TargetBackendInstanceId == user.BackendInstanceId && (!availableOnly || db.ProviderAccounts.Any(account =>
                account.OwnerUserId == user.Id && account.Enabled && account.ProviderId == link.SourceProviderId &&
                (account.Id == link.ProviderAccountId || link.ProviderAccountId == null &&
                    (link.SourceProviderId == "spotify" || link.SourceProviderId == "apple-musickit")))));

    internal async Task RepairExternalAppsAsync(ProtocolExecutionContext context, CancellationToken token, IReadOnlyList<PersonalMix>? feeds = null)
    {
        if (context.Actor?.EffectiveUserId is not { } owner) return;
        await using var repair = await feedReads.AcquireAsync("apps:" + owner, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await BrowseUserAsync(db, owner, token, context);
        if (user == null) return;
        var links = await ExternalLinks(db, user).Where(link => link.Enabled && link.TargetPlaylistId == null).ToArrayAsync(token);
        if (links.Length == 0) return;
        var kinds = await PlaylistSourceKinds.LatestAsync(db, links.Select(link => link.Id).ToArray(), token);
        var publish = (bool)(await settings.GetAsync(PublishKey, token)).Value;
        links = links.Where(link => context.Policy?.IsEnabled(link.SourceProviderId) != false && (IsExternalLink(link) ? publish :
            (link.SourceProviderId is "spotify" or "apple-musickit") && (kinds.GetValueOrDefault(link.Id) == PlaylistSourceKinds.Feed ||
                feeds?.Any(feed => (feed.ProviderAccountId == link.ProviderAccountId || link.ProviderAccountId == null) &&
                    feed.Source == link.SourceProviderId && feed.SourcePlaylistId == link.SourcePlaylistId) == true))).ToArray();
        foreach (var link in links.Where(link => link.Mode != PlaylistLinkMode.Virtual || link.ProjectionMode == PlaylistProjectionMode.Target))
        {
            link.Mode = PlaylistLinkMode.Virtual;
            if (link.ProjectionMode == PlaylistProjectionMode.Target) link.ProjectionMode = PlaylistProjectionMode.Resolved;
            link.UpdatedAt = clock.UtcNow;
            link.Revision++;
        }
        await db.SaveChangesAsync(token);
        if (jobQueue == null || links.Length == 0) return;
        var ids = links.Select(link => link.Id).ToArray();
        var published = await db.PlaylistSourceSnapshots.AsNoTracking().Where(snapshot => ids.Contains(snapshot.PlaylistLinkId) &&
            snapshot.OwnerUserId == owner && snapshot.PublishedAt.HasValue).Select(snapshot => snapshot.PlaylistLinkId).Distinct().ToArrayAsync(token);
        var pending = await db.Jobs.AsNoTracking().Where(job => job.OwnerUserId == owner &&
            (job.Type == "playlist.materialize" || job.Type == DurableScheduleEngine.PlaylistSyncJobType) &&
            (job.State == DurableJobState.Pending || job.State == DurableJobState.Running || job.State == DurableJobState.RetryScheduled))
            .Select(job => job.PayloadJson).ToArrayAsync(token);
        var refreshing = pending.Select(PlaylistRefreshLink).ToHashSet();
        foreach (var link in links.Where(link => !published.Contains(link.Id) && !refreshing.Contains(link.Id)))
            await jobQueue.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>("playlist.materialize",
                $"service-feed-publication:{link.Id:N}:{link.Revision}", new(link.Id, Math.Max(1, link.UpdatedAt.UtcTicks + 1)), owner,
                ProviderAccountId: link.ProviderAccountId, Capability: "playlist"), token);
    }

    private async Task<IReadOnlyList<PersonalMix>> ExternalMixesAsync(UserRecord user, CancellationToken token, Guid? id = null, bool cardsOnly = false, IReadOnlyList<PersonalMix>? feeds = null)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var available = await ExternalLinks(db, user, availableOnly: false).Where(link => id == null || link.Id == id).AsNoTracking()
            .OrderByDescending(link => link.Enabled).ThenBy(link => link.CreatedAt).ToArrayAsync(token);
        var kinds = await PlaylistSourceKinds.LatestAsync(db, available.Select(link => link.Id).ToArray(), token);
        var links = available.Where(link => IsExternalLink(link) || (link.SourceProviderId is "spotify" or "apple-musickit") && kinds.GetValueOrDefault(link.Id) == PlaylistSourceKinds.Feed ||
            feeds?.Any(feed => (feed.ProviderAccountId == link.ProviderAccountId || link.ProviderAccountId == null) && feed.Source == link.SourceProviderId && feed.SourcePlaylistId == link.SourcePlaylistId) == true).ToArray();
        if (links.Length == 0) return [];
        var reader = new DurablePlaylistProjectionReader(factory, libraryAccess, providers, policies);
        var policy = await policies.ResolveForUserAsync(user.Id, token);
        var publish = (bool)(await settings.GetAsync(PublishKey, token)).Value;
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(account => account.OwnerUserId == user.Id && account.Enabled).ToArrayAsync(token);
        var aliases = spotifyImportSettings?.Value is { Enabled: true } imports && user.BackendType == "jellyfin"
            ? imports.Playlists.Where(alias => !string.IsNullOrWhiteSpace(alias.JellyfinId) &&
                (string.IsNullOrWhiteSpace(alias.UserId) || alias.UserId.Equals(user.BackendPrincipalId, StringComparison.OrdinalIgnoreCase)) &&
                links.Any(link => link.SourceProviderId == "spotify" && link.SourcePlaylistId == alias.Id)).ToArray() : [];
        // One playlist listing answers both questions: which native aliases exist and whether saved playlists are still there.
        var wanted = aliases.Select(alias => alias.JellyfinId).Concat(links.Select(link => link.TargetPlaylistId).OfType<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleNative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listedNative = false;
        if (wanted.Count > 0 && playlistTargets != null)
        {
            var target = playlistTargets.Resolve(user.BackendType);
            var targetContext = new BackendPlaylistTargetContext(user.BackendInstanceId, user.BackendPrincipalId);
            for (var offset = 0; offset < 10000; offset += 200)
            {
                var page = target is JellyfinPlaylistTarget jellyfin
                    ? await jellyfin.ListPageAsync(targetContext, null, offset, 200, token, includeWriteAccess: false)
                    : await target.ListPageAsync(targetContext, null, offset, 200, token);
                if (!page.IsSuccess || page.Value == null) break;
                visibleNative.UnionWith(page.Value.Select(item => item.BackendPlaylistId));
                listedNative = page.Value.Count < 200 || wanted.IsSubsetOf(visibleNative);
                if (listedNative) break;
            }
        }
        var projections = id.HasValue
            ? new Dictionary<Guid, DurablePlaylistProjection>()
            : await reader.ReadByLinkIdsAsync(user.Id, links.Select(link => link.Id).ToArray(), token, user.Id, preferLocalCopies: !cardsOnly);
        var result = new List<PersonalMix>(links.Length);
        foreach (var link in links)
        {
            var projection = id.HasValue
                ? await reader.ReadByLinkIdAsync(user.Id, link.Id, token, user.Id)
                : projections.GetValueOrDefault(link.Id);
            var songs = projection?.Entries.Where(entry => entry.RouteKind is "local" or "external").Select(entry =>
            {
                var metadata = projection.SourceEntries.FirstOrDefault(item => item.Position == entry.Position)?.Metadata;
                return new Song
                {
                    Id = entry.BackendItemId ?? $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}",
                    Title = entry.Title,
                    Artist = entry.Artists.FirstOrDefault() ?? "",
                    Artists = entry.Artists.ToList(),
                    Album = entry.Album ?? "",
                    Duration = (int)((entry.DurationMilliseconds ?? 0) / 1000),
                    IsLocal = entry.RouteKind == "local",
                    ExternalProvider = entry.RouteProviderId,
                    ExternalId = entry.ExternalId,
                    CanonicalRecordingId = entry.CanonicalRecordingId,
                    Isrc = entry.Isrc,
                    ExplicitContentLyrics = metadata?.IsExplicit is { } explicitContent ? explicitContent ? 1 : 0 : null
                };
            }).ToArray() ?? [];
            if (policy.Preferences.ExplicitFilter == "CleanOnly")
            {
                var context = await ContextAsync(user, token);
                foreach (var song in songs)
                {
                    var selected = !song.IsLocal && song.ExternalProvider != null && song.ExternalId != null
                        ? await providers.GetStoredSongAsync(context, song.ExternalProvider, song.ExternalId) : null;
                    song.ExplicitContentLyrics = selected?.ExplicitContentLyrics;
                    if (selected != null && CleanRecordingPolicy.IsVerifiedClean(selected))
                    {
                        if (!string.IsNullOrWhiteSpace(selected.Title)) song.Title = selected.Title;
                        if (!string.IsNullOrWhiteSpace(selected.Artist)) song.Artist = selected.Artist;
                        if (!string.IsNullOrWhiteSpace(selected.Album)) song.Album = selected.Album;
                    }
                }
            }
            var blocked = await taste.BlockedSongsAsync(user.Id, songs, token);
            var items = songs.Where(song => !blocked.Contains(song) && policy.Includes(song))
                .Select(song => new PersonalMixItem(link.Id, projection!.SnapshotVersion, SafeSong(song),
                    link.SourceProviderId, "From your linked playlist", 0)).ToArray();
            var nativeAlias = link.SourceProviderId == "spotify" ? aliases.FirstOrDefault(alias =>
                alias.Id == link.SourcePlaylistId && visibleNative.Contains(alias.JellyfinId))?.JellyfinId : null;
            var nativeReady = nativeAlias != null;
            var appsUnavailable = !accounts.Any(account => account.ProviderId == link.SourceProviderId &&
                (account.Id == link.ProviderAccountId || link.ProviderAccountId == null))
                ? "Your music service connection is disabled or unavailable."
                : IsExternalLink(link) && !publish ? "Your admin hasn't allowed publishing mixes to apps." : null;
            if (appsUnavailable == null && link.TargetPlaylistId != null && playlistTargets != null)
            {
                // A failed listing says nothing about the playlist, so only a complete listing can call it missing.
                if (listedNative && !visibleNative.Contains(link.TargetPlaylistId)) appsUnavailable = "The playlist in your music server is unavailable.";
                else nativeReady = true;
            }
            if (appsUnavailable == null && projection == null && !nativeReady) appsUnavailable = "Preparing this mix for your apps.";
            result.Add(new(link.Id, "external", PlaylistVirtualizationService.DisplayName(link.SourceProviderId, projection?.Name ?? ExternalName(link)),
                "From " + ExternalSourceLabel(link), projection?.SnapshotVersion ?? 0,
                projection?.RetrievedAt, new(items.Length > 0, items.Length > 0 ? "Your linked playlist is ready." : "Waiting for playable songs from your linked playlist."),
                link.Enabled, items)
            {
                Source = link.SourceProviderId,
                SourcePlaylistId = link.SourcePlaylistId,
                ProviderAccountId = link.ProviderAccountId,
                AppsUnavailableReason = appsUnavailable,
                PlaylistId = link.TargetPlaylistId ?? nativeAlias ?? PlaylistVirtualizationService.CreateProtocolId(link.Id),
                SourceLabel = ExternalSourceLabel(link),
                ArtworkUrl = projection?.ArtworkReferenceKey == null ? null : ServiceArtworkUrl(link.ProviderAccountId,
                    link.SourceProviderId, link.SourcePlaylistId, projection.ArtworkReferenceKey),
                Evidence = new(MixRecipes.For("external"), 0, 0, null, null, null, 0, 0,
                    projection?.SourceEntries.Count ?? items.Length, items.Length, items.Length, items.Count(item => item.Song.IsLocal), items.Count(item => !item.Song.IsLocal),
                    new Dictionary<string, int>(), null, null, null)
            });
        }
        return result;
    }

    internal static PersonalMix PendingExternalMix(PlaylistLinkRecord link) => new(link.Id, "external", ExternalName(link),
        "From " + ExternalSourceLabel(link), 0, null, new(false, "Matching songs…"), true, [])
    { SourceLabel = ExternalSourceLabel(link), AppsUnavailableReason = "Preparing this mix for your apps." };

    private static string ExternalSourceLabel(PlaylistLinkRecord link) => link.SourceProviderId switch
    { "lastfm" => "Last.fm", "listenbrainz" => "ListenBrainz", "spotify" => "Spotify", "apple-musickit" => "Apple Music", _ => "your music service" };

    private static string ExternalName(PlaylistLinkRecord link) => link.SourceProviderId is not ("lastfm" or "listenbrainz") ? "Service playlist" : link.SourceProviderId == "listenbrainz"
        ? link.SourcePlaylistId == Providers.ListenBrainz.ListenBrainzPlaylistCapabilityAdapter.Recommended ? "Recommended for you"
            : link.SourcePlaylistId == Providers.ListenBrainz.ListenBrainzPlaylistCapabilityAdapter.FreshReleases ? "Fresh releases"
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(link.SourcePlaylistId[11..].Replace('-', ' '))
        : link.SourcePlaylistId switch
        {
            "loved" => "Loved on Last.fm",
            "similar" => "Similar to your top tracks",
            _ => "Your top tracks — " + Providers.LastFm.LastFmPlaylistCapabilityAdapter.PeriodLabel(link.SourcePlaylistId.Split(':')[1])
        };

    private async Task<bool> SetExternalInAppsAsync(UserRecord user, Guid id, bool enabled, CancellationToken token, string timeZoneId)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var link = await ExternalLinks(db, user, availableOnly: enabled).SingleOrDefaultAsync(item => item.Id == id, token);
        if (link == null || !IsExternalLink(link) && link.SourceProviderId is not ("spotify" or "apple-musickit")) return false;
        if (!IsExternalLink(link) && (await PlaylistSourceKinds.LatestAsync(db, [link.Id], token)).GetValueOrDefault(link.Id) != PlaylistSourceKinds.Feed &&
            !(await ServiceFeedCardsAsync(user, token)).Any(feed => (feed.ProviderAccountId == link.ProviderAccountId || link.ProviderAccountId == null) && feed.Source == link.SourceProviderId && feed.SourcePlaylistId == link.SourcePlaylistId)) return false;
        if (enabled && playlistSync != null && await PlaylistAppRemoval.FindAsync(db, link, token) != null)
        {
            await playlistSync.RestoreToAppsAsync((await ContextAsync(user, token)) with { IsReadOnly = false }, link.Id, link.Revision, token);
            await db.Entry(link).ReloadAsync(token);
        }
        if (link.Enabled == enabled && (!enabled || link.ScheduleId != null)) return true;
        JobScheduleRecord? schedule = null;
        if (link.ScheduleId is { } scheduleId)
        {
            schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == scheduleId &&
                item.OwnerUserId == user.Id && item.JobType == DurableScheduleEngine.PlaylistSyncJobType, token);
            if (schedule == null) throw new UnauthorizedAccessException();
        }
        if (schedule == null && enabled)
        {
            schedule = new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user.Id,
                JobType = DurableScheduleEngine.PlaylistSyncJobType,
                CronExpression = "0 4 * * *",
                TimeZoneId = timeZoneId,
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                CreatedAt = clock.UtcNow
            };
            db.JobSchedules.Add(schedule);
            link.ScheduleId = schedule.Id;
        }
        if (schedule != null)
        {
            schedule.Enabled = enabled;
            schedule.NextRunAt = enabled ? DurableScheduleEngine.GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, clock.UtcNow) : null;
            schedule.UpdatedAt = clock.UtcNow;
            schedule.Revision++;
        }
        link.Enabled = enabled;
        link.UpdatedAt = clock.UtcNow;
        link.Revision++;
        await db.SaveChangesAsync(token);
        clientEvents?.Publish(user.Id, ClientEvent.Mix(id));
        return true;
    }

    internal async Task<bool> IsExternalVisibleAsync(ProtocolExecutionContext context, PlaylistLinkRecord link, CancellationToken token) =>
        !(await HiddenExternalAsync(context, [link], token)).Contains(link.Id);

    // Checks a whole playlist list at once so listing stays a fixed number of queries. Ordinary playlists stay
    // visible; mixes and service feeds need their owner's enabled link and a connected account.
    internal async Task<IReadOnlySet<Guid>> HiddenExternalAsync(ProtocolExecutionContext context, IReadOnlyCollection<PlaylistLinkRecord> links,
        CancellationToken token)
    {
        var hidden = new HashSet<Guid>();
        if (links.Count == 0) return hidden;
        await using var db = await factory.CreateDbContextAsync(token);
        var kinds = await PlaylistSourceKinds.LatestAsync(db, links.Where(link => !IsExternalLink(link) &&
            link.ImportMode == PlaylistImportMode.Linked && link.SourceProviderId is "spotify" or "apple-musickit").Select(link => link.Id).ToArray(), token);
        var gated = links.Where(link => IsExternalLink(link) || kinds.GetValueOrDefault(link.Id) == PlaylistSourceKinds.Feed).ToArray();
        if (gated.Length == 0) return hidden;
        var owner = context.Actor?.EffectiveUserId;
        var owned = gated.Where(link => link.OwnerUserId == owner).ToArray();
        hidden.UnionWith(gated.Where(link => link.OwnerUserId != owner).Select(link => link.Id));
        if (owned.Length == 0) return hidden;
        if (owned.Any(IsExternalLink) && !(bool)(await settings.GetAsync(PublishKey, token)).Value)
        {
            hidden.UnionWith(owned.Where(IsExternalLink).Select(link => link.Id));
            owned = owned.Where(link => !IsExternalLink(link)).ToArray();
            if (owned.Length == 0) return hidden;
        }
        var user = await BrowseUserAsync(db, owner!.Value, token, context);
        if (user == null)
        {
            hidden.UnionWith(owned.Select(link => link.Id));
            return hidden;
        }
        var row = await RowAsync(db, user, token);
        if (row != null && System.Text.Json.JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!.AppsOptInVersion < 3)
            await SnapshotAsync(user.Id, token);
        var ids = owned.Select(link => link.Id).ToArray();
        var enabled = (await db.PlaylistLinks.AsNoTracking().Where(item => ids.Contains(item.Id) && item.Enabled)
            .Select(item => item.Id).ToArrayAsync(token)).ToHashSet();
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(account => account.OwnerUserId == user.Id && account.Enabled)
            .Select(account => new { account.Id, account.ProviderId }).ToArrayAsync(token);
        hidden.UnionWith(owned.Where(link => !enabled.Contains(link.Id) || !accounts.Any(account =>
            (account.Id == link.ProviderAccountId || link.ProviderAccountId == null) && account.ProviderId == link.SourceProviderId)).Select(link => link.Id));
        return hidden;
    }

    internal async Task<VirtualPlaylistReadModel> FilterExternalTracksAsync(ProtocolExecutionContext context,
        VirtualPlaylistReadModel playlist, CancellationToken token)
    {
        var owner = context.Actor!.EffectiveUserId!.Value;
        var songs = playlist.Tracks.Select(track => new Song
        {
            Id = track.BackendItemId,
            Title = track.Title,
            Artist = track.Artist,
            Artists = [track.Artist],
            Album = track.Album ?? "",
            IsLocal = track.RouteKind == allstarr.Core.Matching.TrackRouteKind.Local,
            ExternalProvider = track.RouteProviderId,
            ExternalId = track.RouteExternalId,
            CanonicalRecordingId = track.SourceMetadata?.CanonicalRecordingId,
            ExplicitContentLyrics = track.PlaybackExplicitContentLyrics ?? (track.SourceMetadata?.IsExplicit is { } explicitContent
                ? explicitContent ? 1 : 0 : null)
        }).ToArray();
        var blocked = await taste.BlockedSongsAsync(owner, songs, token);
        var policy = await policies.ResolveForUserAsync(owner, token);
        if (policy.Preferences.ExplicitFilter == "CleanOnly")
            for (var index = 0; index < songs.Length; index++)
            {
                var track = playlist.Tracks[index];
                if (track.RouteKind == allstarr.Core.Matching.TrackRouteKind.Local)
                    songs[index].ExplicitContentLyrics = track.NativeEntryJson != null &&
                        System.Text.Json.Nodes.JsonNode.Parse(track.NativeEntryJson) is System.Text.Json.Nodes.JsonObject native
                            ? CleanOnlyProtocolContent.Rating(native, context.Protocol) : null;
                else if (track.PlaybackExplicitContentLyrics == null)
                {
                    var selected = track.RouteProviderId != null && track.RouteExternalId != null
                        ? await providers.GetStoredSongAsync(context, track.RouteProviderId, track.RouteExternalId) : null;
                    songs[index].ExplicitContentLyrics = selected?.ExplicitContentLyrics;
                    if (selected != null && CleanRecordingPolicy.IsVerifiedClean(selected))
                    {
                        if (!string.IsNullOrWhiteSpace(selected.Title)) songs[index].Title = selected.Title;
                        if (!string.IsNullOrWhiteSpace(selected.Artist)) songs[index].Artist = selected.Artist;
                        if (!string.IsNullOrWhiteSpace(selected.Album)) songs[index].Album = selected.Album;
                    }
                }
            }
        return playlist with
        {
            Tracks = playlist.Tracks.Select((track, index) => (Track: track, Song: songs[index]))
                .Where(item => !blocked.Contains(item.Song) && policy.Includes(item.Song))
                .Select(item => policy.Preferences.ExplicitFilter == "CleanOnly" ? item.Track with
                {
                    Title = item.Song.Title,
                    Artist = item.Song.Artist,
                    Album = item.Song.Album,
                    PlaybackExplicitContentLyrics = item.Song.ExplicitContentLyrics
                } : item.Track).ToArray()
        };
    }
}
