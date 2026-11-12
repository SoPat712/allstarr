using allstarr.Core.Operations;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientEvents;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.ListenBrainz;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    private readonly KeyedAsyncLock feedReads = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> feedAccounts = new();
    private static readonly TimeSpan FeedWindow = TimeSpan.FromHours(6);

    private ProviderExecutionContext FeedContext(ProtocolExecutionContext execution, ProviderAccountRecord account, CancellationToken token) =>
        new(execution.RequireActor(), account.ProviderId,
            new(account.Id, account.ProviderId, account.Scope, account.Revision, account.Enabled, account.OwnerUserId, "service-feeds", account.SecretReferenceId),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                false, false, false, [account.ProviderId]), "service-feeds", execution.CorrelationId, clock.UtcNow.AddMinutes(5), token);

    private async Task<PersonalMix[]> ServiceFeedCardsAsync(UserRecord user, CancellationToken token, bool refresh = true, bool queuePreparation = true)
    {
        if (registry == null) return [];
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            var context = await ContextAsync(user, token);
            var accounts = await db.ProviderAccounts.AsNoTracking().Where(account => account.OwnerUserId == user.Id &&
                account.Enabled &&
                (account.ProviderId == "spotify" || account.ProviderId == "apple-musickit" || account.ProviderId == "lastfm" || account.ProviderId == "listenbrainz"))
                .OrderBy(account => account.CreatedAt).ThenBy(account => account.Id).ToArrayAsync(token);
            var result = new List<PersonalMix>();
            foreach (var account in accounts)
            {
                if (context.Policy?.IsEnabled(account.ProviderId) == false ||
                    !registry.TryGetCapability<IProviderPlaylistCapability>(account.ProviderId, ProviderCapabilityKind.Playlist, out var capability)) continue;
                var key = Hash(Scope(user) + ":" + account.Revision);
                var revision = account.Revision.ToString(CultureInfo.InvariantCulture) + ":artwork-v1";
                var rows = db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == user.Id && item.ProviderAccountId == account.Id &&
                    item.ResourceKind == "service-feeds" && item.ExternalIdHash == key);
                var saved = await rows.OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
                var cards = saved == null ? [] : JsonSerializer.Deserialize<PersonalMix[]>(saved.PayloadJson)!;
                var due = refresh && (saved == null || saved.ProviderRevision != revision || clock.UtcNow - saved.RetrievedAt >= FeedWindow);
                // The background refresh job also prepares the feed's songs, so it replaces the preparation queued below.
                var queued = false;
                if (due && saved != null && queuePreparation && jobQueue != null)
                {
                    var generation = new DateTimeOffset(clock.UtcNow.UtcTicks / FeedWindow.Ticks * FeedWindow.Ticks, TimeSpan.Zero);
                    await QueueServiceFeedPreparationAsync(user.Id, account, 0, generation, token);
                    due = false;
                    queued = true;
                }
                if (due)
                {
                    await using var refreshLock = await feedReads.AcquireAsync("cards:" + account.Id, token);
                    saved = await rows.AsNoTracking().OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
                    cards = saved == null ? [] : JsonSerializer.Deserialize<PersonalMix[]>(saved.PayloadJson)!;
                    if (saved != null && saved.ProviderRevision == revision && clock.UtcNow - saved.RetrievedAt < FeedWindow)
                    {
                        if (queuePreparation) await QueueServiceFeedPreparationAsync(user.Id, account, 0, saved.RetrievedAt, token);
                        result.AddRange(cards);
                        continue;
                    }
                    var fetched = new List<PersonalMix>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var execution = FeedContext(context, account, token);
                    string? cursor = null;
                    var complete = false;
                    var requests = feedAccounts.GetOrAdd(account.Id, _ => new(2, 2));
                    using (RequestTiming.Current?.Stage("lock")) await requests.WaitAsync(token);
                    try
                    {
                        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
                        {
                            var page = new ProviderPageRequest(100, cursor);
                            var outcome = capability is AppleMusicKitPlaylistCapabilityAdapter apple
                                ? await apple.GetServiceFeedsAsync(execution, page)
                                : capability is ListenBrainzPlaylistCapabilityAdapter listenBrainz
                                    ? await listenBrainz.GetServiceFeedsAsync(execution, page)
                                    : capability is SpotifyPlaylistCapabilityAdapter spotify
                                        ? await spotify.GetServiceFeedsAsync(execution, page)
                                        : await capability!.GetUserPlaylistsAsync(execution, new(page));
                            if (!outcome.IsSuccess) break;
                            var value = outcome.RequireValue();
                            foreach (var item in value.Items.Where(item => item.Id.ProviderId == account.ProviderId &&
                                         PlaylistSourceKinds.Classify(account.ProviderId, item.Id.Value, item.Owner.ProviderUserId, null) == PlaylistSourceKinds.Feed))
                            {
                                var id = Id(user, $"service-feed:{account.Id}:{item.Id.Value}");
                                fetched.Add(new(id, "external", PlaylistVirtualizationService.DisplayName(account.ProviderId, item.Name), "From " + SourceName(account.ProviderId), 1,
                                    item.SourceRevision.Contains('T') && DateTimeOffset.TryParse(item.SourceRevision, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated) ? updated : null,
                                    new(item.TrackCount != 0, "Open to see songs from your service."), false, [])
                                {
                                    Source = account.ProviderId,
                                    SourceLabel = SourceName(account.ProviderId),
                                    SourcePlaylistId = item.Id.Value,
                                    ProviderAccountId = account.Id,
                                    ArtworkUrl = item.Artwork == null ? null : ServiceArtworkUrl(account.Id, account.ProviderId, item.Id.Value, item.Artwork.Revision ?? item.SourceRevision),
                                    SongCount = item.TrackCount,
                                    SavedFrom = SavedFeedOwner(item, user, account)
                                });
                            }
                            if (value.NextCursor == null) { complete = !value.IsPartial; break; }
                            if (!seen.Add(value.NextCursor)) break;
                            cursor = value.NextCursor;
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { logger?.LogDebug("Service feeds could not refresh for {Provider}", account.ProviderId); }
                    finally { requests.Release(); }
                    if (complete) cards = fetched.DistinctBy(item => item.SourcePlaylistId).ToArray();
                    var json = JsonSerializer.Serialize(cards, DocumentJson);
                    var version = (saved?.SnapshotVersion ?? 0) + 1;
                    saved = new()
                    {
                        Id = Guid.CreateVersion7(),
                        OwnerUserId = user.Id,
                        ProviderAccountId = account.Id,
                        ProviderId = account.ProviderId,
                        ResourceKind = "service-feeds",
                        ExternalIdHash = key,
                        SnapshotVersion = version,
                        Protocol = user.BackendType,
                        BackendInstanceId = user.BackendInstanceId,
                        BackendPrincipalId = user.BackendPrincipalId,
                        ProviderRevision = revision,
                        CorrelationId = "service-feeds"
                    };
                    db.ExternalMetadataSnapshots.Add(saved);
                    saved.PayloadJson = json;
                    saved.PayloadSha256 = Hash(json);
                    saved.RetrievedAt = clock.UtcNow;
                    await db.SaveChangesAsync(token);
                    if (complete && coverImages != null) WarmCovers(user.Id, cards);
                    await rows.Where(item => item.SnapshotVersion <= version - KeptDocuments).ExecuteDeleteAsync(token);
                }
                if (queuePreparation && saved != null && !queued)
                    await QueueServiceFeedPreparationAsync(user.Id, account, 0, saved.RetrievedAt, token);
                result.AddRange(cards);
            }
            return result.ToArray();
        }
    }

    private async Task<IReadOnlyList<PersonalMix>> ServiceMixesAsync(UserRecord user, CancellationToken token, Guid? id = null, bool cardsOnly = false, bool background = false, bool coverOnly = false)
    {
        var cards = await ServiceFeedCardsAsync(user, token, refresh: !background && !coverOnly && id == null,
            queuePreparation: !background && !coverOnly);
        if (!coverOnly) await RepairExternalAppsAsync(await ContextAsync(user, token), token, cards);
        var linked = await ExternalMixesAsync(user, token, id, cardsOnly, cards);
        await using var db = await factory.CreateDbContextAsync(token);
        var links = await ExternalLinks(db, user, availableOnly: false).AsNoTracking().OrderByDescending(link => link.Enabled).ThenBy(link => link.CreatedAt).ToArrayAsync(token);
        var result = linked.ToList();
        var prepared = id.HasValue ? null : await PreparedServiceFeedsAsync(user, cards.Where(card =>
        {
            var link = links.FirstOrDefault(link => (link.ProviderAccountId == card.ProviderAccountId || link.ProviderAccountId == null) &&
                link.SourceProviderId == card.Source && link.SourcePlaylistId == card.SourcePlaylistId);
            var mix = link == null ? null : linked.FirstOrDefault(mix => mix.Id == link.Id);
            return link == null || mix != null && (mix.Generation == 0 || !link!.Enabled);
        }).ToArray(), token);
        foreach (var sourceCard in cards)
        {
            var card = sourceCard;
            var link = links.FirstOrDefault(link => (link.ProviderAccountId == card.ProviderAccountId || link.ProviderAccountId == null) && link.SourceProviderId == card.Source && link.SourcePlaylistId == card.SourcePlaylistId);
            if (link != null)
            {
                if (id == card.Id && !result.Any(mix => mix.Id == link.Id))
                    result.AddRange(await ExternalMixesAsync(user, token, link.Id, cardsOnly, cards));
                var match = result.FindIndex(mix => mix.Id == link.Id);
                if (match >= 0)
                {
                    var mix = result[match];
                    if (mix.Generation == 0 || !link.Enabled)
                    {
                        card = prepared == null ? await PreparedServiceFeedAsync(user, card, token) : prepared[card.Id];
                        mix = card with
                        {
                            Id = link.Id,
                            Added = link.Enabled,
                            AppsUnavailableReason = mix.AppsUnavailableReason,
                            PlaylistId = mix.PlaylistId,
                            Songs = card.Songs.Select(item => item with { MixId = link.Id }).ToArray()
                        };
                    }
                    result[match] = mix with
                    {
                        Source = card.Source,
                        SourcePlaylistId = card.SourcePlaylistId,
                        ProviderAccountId = card.ProviderAccountId,
                        ArtworkUrl = card.ArtworkUrl ?? mix.ArtworkUrl,
                        SongCount = card.SongCount,
                        SavedFrom = card.SavedFrom
                    };
                }
                continue;
            }
            if (id != null && card.Id != id) continue;
            result.Add(prepared == null ? await PreparedServiceFeedAsync(user, card, token) : prepared[card.Id]);
        }
        return result.DistinctBy(mix => (mix.Source is "spotify" or "apple-musickit" ? (Guid?)null : mix.ProviderAccountId,
            mix.Source, mix.SourcePlaylistId ?? mix.Id.ToString())).ToArray();
    }

    private async Task<PersonalMix> ServiceFeedSongsAsync(UserRecord user, PersonalMix card, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var account = await db.ProviderAccounts.AsNoTracking().SingleAsync(item => item.Id == card.ProviderAccountId && item.OwnerUserId == user.Id && item.Enabled, token);
        var context = await ContextAsync(user, token);
        var execution = FeedContext(context, account, token);
        var capability = registry!.GetRequiredCapability<IProviderPlaylistCapability>(account.ProviderId, ProviderCapabilityKind.Playlist);
        var key = Hash(Scope(user) + ":" + account.Revision + ":" + card.SourcePlaylistId);
        var requests = feedAccounts.GetOrAdd(account.Id, _ => new(2, 2));
        using (RequestTiming.Current?.Stage("lock")) await requests.WaitAsync(token);
        Song[] songs;
        var sourceCount = card.SongCount;
        try
        {
            var rows = db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == user.Id && item.ProviderAccountId == account.Id &&
                item.ResourceKind == "service-feed-songs" && item.ExternalIdHash == key);
            var saved = await rows.AsNoTracking().OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
            songs = saved == null ? [] : JsonSerializer.Deserialize<Song[]>(saved.PayloadJson)!;
            if (saved == null || clock.UtcNow - saved.RetrievedAt >= FeedWindow)
            {
                var tracks = new List<Song>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                do
                {
                    var outcome = await capability.GetPlaylistTracksAsync(execution, new(new(account.ProviderId, ProviderResourceKind.Playlist, card.SourcePlaylistId!), new(100, cursor)));
                    if (!outcome.IsSuccess) throw new PlaylistSourceUnavailableException("feed_unavailable", outcome.Error, account.ProviderId);
                    var value = outcome.RequireValue();
                    sourceCount = value.Playlist.TrackCount ?? sourceCount;
                    var page = value.Tracks;
                    tracks.AddRange(page.Items.Where(item => item.Metadata != null).Select(item => ProtocolProviderGateway.Map(item.Metadata!)));
                    cursor = page.NextCursor;
                    if (cursor != null && (!seen.Add(cursor) || seen.Count >= 1000)) throw new InvalidOperationException("Your service playlist could not finish loading.");
                } while (cursor != null);
                songs = tracks.Select(SafeSong).ToArray();
                var json = JsonSerializer.Serialize(songs, DocumentJson);
                var version = (saved?.SnapshotVersion ?? 0) + 1;
                saved = new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user.Id,
                    ProviderAccountId = account.Id,
                    ProviderId = account.ProviderId,
                    ResourceKind = "service-feed-songs",
                    ExternalIdHash = key,
                    SnapshotVersion = version,
                    Protocol = user.BackendType,
                    BackendInstanceId = user.BackendInstanceId,
                    BackendPrincipalId = user.BackendPrincipalId,
                    ProviderRevision = account.Revision.ToString(CultureInfo.InvariantCulture),
                    CorrelationId = "service-feeds"
                };
                db.ExternalMetadataSnapshots.Add(saved);
                saved.PayloadJson = json;
                saved.PayloadSha256 = Hash(json);
                saved.RetrievedAt = clock.UtcNow;
                await db.SaveChangesAsync(token);
                await rows.Where(item => item.SnapshotVersion <= version - KeptDocuments).ExecuteDeleteAsync(token);
            }
        }
        finally { requests.Release(); }
        foreach (var song in songs)
            if (identities != null && song.ExternalId is { } externalId)
            {
                var source = new ProviderExternalResourceId(account.ProviderId, ProviderResourceKind.Track, externalId);
                try
                {
                    song.CanonicalRecordingId = await identities.RememberSourceAsync(execution, source, song.Id, ProviderIdentityScope.Account, song.Isrc, token);
                    await identities.RememberPlaybackMetadataAsync(execution, source, song, token);
                }
                catch (RecordingMergeConflictException) { }
            }
        var projected = (await PreferLocalCopiesAsync(user.Id, [card with { Songs = songs.Select(song => new PersonalMixItem(card.Id, 1, song, account.ProviderId, "From your service playlist", 0)).ToArray() }], token)).Single();
        projected = await MatchServiceFeedLibraryAsync(user, projected, context, token);
        var blocked = await taste.BlockedSongsAsync(user.Id, projected.Songs.Select(item => item.Song).ToArray(), token);
        var playable = new List<PersonalMixItem>();
        foreach (var item in projected.Songs)
            if (!blocked.Contains(item.Song) && context.Policy!.Includes(item.Song) && (item.Song.IsLocal || await providers.CanPlaySongAsync(context, item.Song)))
            {
                if (!item.Song.IsLocal && item.Song.CanonicalRecordingId is { } recording && recording != Guid.Empty)
                {
                    item.Song.Id = "ext-allstarr-song-" + recording.ToString("D");
                    item.Song.ExternalProvider = "allstarr";
                    item.Song.ExternalId = recording.ToString("D");
                }
                playable.Add(item);
            }
        return projected with
        {
            Songs = playable,
            SongCount = sourceCount,
            Evidence = new(MixRecipes.For("external"), 0, 0, null, null, null, 0, 0, songs.Length, playable.Count, playable.Count,
                playable.Count(item => item.Song.IsLocal), playable.Count(item => !item.Song.IsLocal), new Dictionary<string, int>(), null, null, null),
            Readiness = new(playable.Count > 0, playable.Count > 0 ? "Ready" : "No playable songs right now.")
        };
    }

    private async Task<bool> AddServiceFeedAsync(UserRecord user, Guid id, bool enabled, CancellationToken token, string timeZone)
    {
        var card = (await ServiceFeedCardsAsync(user, token)).SingleOrDefault(item => item.Id == id);
        if (card == null) return false;
        await using var db = await factory.CreateDbContextAsync(token);
        var existing = await ExternalLinks(db, user).AsNoTracking().FirstOrDefaultAsync(link => (link.ProviderAccountId == card.ProviderAccountId || link.ProviderAccountId == null) &&
            link.SourceProviderId == card.Source && link.SourcePlaylistId == card.SourcePlaylistId, token);
        if (existing != null) return await SetExternalInAppsAsync(user, existing.Id, enabled, token, timeZone);
        if (!enabled) return true;
        if (playlistStore == null || jobQueue == null) throw new InvalidOperationException("Playlist syncing is unavailable.");
        var context = (await ContextAsync(user, token)) with { IsReadOnly = false };
        var link = await playlistStore.CreateLinkAsync(context, new(card.ProviderAccountId, card.Source!, card.SourcePlaylistId!, Hash(card.SourcePlaylistId!),
            user.BackendType, user.BackendInstanceId, PlaylistLinkMode.Virtual, PlaylistMaterializationMode.Reconcile, "playlist-rules-v1", "playlist-policy-v1"), token);
        await SetExternalInAppsAsync(user, link.Id, true, token, timeZone);
        await jobQueue.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>("playlist.materialize", $"initial-import:{link.Id:N}",
            new(link.Id, Math.Max(1, link.CreatedAt.UtcTicks)), user.Id, ProviderAccountId: link.ProviderAccountId, Capability: "playlist"), token);
        clientEvents?.Publish(user.Id, ClientEvent.Mix(id));
        return true;
    }
}
