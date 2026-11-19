using allstarr.Core.Artwork;
using allstarr.Core.Caching;
using allstarr.Core.Operations;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed class ItemDetailsReader(
    IDbContextFactory<AllstarrDbContext> factory,
    ITrackIdentityService identities,
    IEffectiveProviderPolicyResolver policies,
    IProviderRegistry registry,
    FavoriteViewReader favorites,
    JellyfinProxyService jellyfin,
    IApplicationCache cache,
    IDurableRuntimeSettings settings,
    ItemMatchReader matches,
    IProtocolProviderGateway? gateway = null, ILibraryIndexService? libraryIndex = null, LyricsRefreshStore? refreshStore = null,
    ILogger<ItemDetailsReader>? logger = null)
{
    // Every snapshot column but the payload, the largest by far.
    internal static readonly System.Linq.Expressions.Expression<Func<ExternalMetadataSnapshotRecord, ExternalMetadataSnapshotRecord>> WithoutPayload =
        item => new ExternalMetadataSnapshotRecord
        {
            Id = item.Id,
            OwnerUserId = item.OwnerUserId,
            ProviderAccountId = item.ProviderAccountId,
            ProviderTrackIdentityId = item.ProviderTrackIdentityId,
            SourceJobId = item.SourceJobId,
            BackendInstanceId = item.BackendInstanceId,
            BackendPrincipalId = item.BackendPrincipalId,
            Protocol = item.Protocol,
            ProviderId = item.ProviderId,
            ResourceKind = item.ResourceKind,
            ExternalIdHash = item.ExternalIdHash,
            SnapshotVersion = item.SnapshotVersion,
            ProviderRevision = item.ProviderRevision,
            PayloadSha256 = item.PayloadSha256,
            CorrelationId = item.CorrelationId,
            RetrievedAt = item.RetrievedAt
        };

    public async Task<ItemDetailsResponse> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string[] ids,
        IReadOnlyDictionary<string, Song>? observedSongs = null) =>
        (await ReadWithSongsAsync(http, execution, ids, resolveServingProvider: true, observedSongs)).Response;

    internal async Task<(ItemDetailsResponse Response, IReadOnlyDictionary<string, Song> Songs)> ReadWithSongsAsync(
        HttpContext http, ProtocolExecutionContext execution, string[] ids, bool resolveServingProvider = false,
        IReadOnlyDictionary<string, Song>? observedSongs = null)
    {
        using var building = RequestTiming.Current?.Stage("build");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        long matchTicks = 0, lyricsTicks = 0, lookupTicks = 0, fileTicks = 0, countTicks = 0, itemTicks = 0;
        var steps = new List<string>();
        var mark = started;
        void Step(string name)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            steps.Add($"{name} {(int)System.Diagnostics.Stopwatch.GetElapsedTime(mark, now).TotalMilliseconds}");
            mark = now;
        }
        var token = execution.CancellationToken;
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var protocol = execution.Protocol.ToString().ToLowerInvariant();
        var requested = ids.Distinct(StringComparer.Ordinal).ToArray();
        var external = requested.Where(id => id.Length <= 500).Select(id => (Id: id, Parsed: LocalLibraryService.ParseExternalResource(id)))
            .Where(item => item.Parsed.isExternal && item.Parsed.type == "song" && ValidProvider(item.Parsed.provider!))
            .Select(item => new Source(item.Id, registry.ResolveProviderId(item.Parsed.provider!), item.Parsed.externalId!)).ToArray();
        IReadOnlyDictionary<string, string> localIds = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, CanonicalRecordingIdentity?> known = new Dictionary<string, CanonicalRecordingIdentity?>(StringComparer.Ordinal);
        if (libraryIndex != null && external.Length > 0)
        {
            known = await identities.ResolveRecordingsAsync(execution.RequireActor(), external.Select(source => source.Id).ToArray(), token);
            var lookups = external.Where(source => known.GetValueOrDefault(source.Id) != null).Select(source => new LibraryRecordingLookup(
                source.Id, known[source.Id]!.Id, known[source.Id]!.Isrc, source.Provider, source.ExternalId)).ToArray();
            if (lookups.Length > 0) localIds = await libraryIndex.FindKnownRecordingTracksAsync(execution, lookups, token);
        }
        Step("library copies");
        var nativeIds = requested.Where(id => !id.StartsWith("ext-", StringComparison.Ordinal) &&
            id.Length is > 0 and <= 500 && !id.Contains(',')).Concat(localIds.Values).Distinct(StringComparer.Ordinal).ToArray();
        var policy = execution.Policy ?? await policies.ResolveForUserAsync(owner, token);
        var native = await ReadNativeAsync(http, execution, nativeIds, policy);
        foreach (var pair in localIds)
            if (native.TryGetValue(pair.Value, out var nativeSaved)) native[pair.Key] = nativeSaved;
        Step("media server");
        var nativeLookupIds = nativeIds.SelectMany(id => Guid.TryParse(id, out var parsed)
            ? new[] { id, parsed.ToString("N"), parsed.ToString("D") } : [id]).Distinct(StringComparer.Ordinal).ToArray();
        await using var db = await factory.CreateDbContextAsync(token);
        var library = nativeLookupIds.Length == 0 ? [] : await db.LibraryTracks.AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.Protocol == protocol && item.BackendInstanceId == execution.BackendInstanceId && nativeLookupIds.Contains(item.BackendItemId))
            .ToArrayAsync(token);
        var resolved = known.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var remaining = external.Select(item => item.Id).Concat(library.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value.ToString("D"))).Where(id => !resolved.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (remaining.Length > 0)
            foreach (var item in await identities.ResolveRecordingsAsync(execution.RequireActor(), remaining, token)) resolved[item.Key] = item.Value;
        var recordingIds = resolved.Values.OfType<CanonicalRecordingIdentity>().Select(item => item.Id).Distinct().ToArray();
        Step("recordings");
        var hashes = external.Select(item => CanonicalCatalogKeys.Hash(item.ExternalId)).Distinct().ToArray();
        // The snapshots behind these songs are found through their hash, their linked identity or a decision naming the
        // recording. Each is an indexed lookup; asking all three per snapshot scanned every snapshot the listener has.
        var linkedIdentities = recordingIds.Length == 0 ? [] : await db.ProviderTrackIdentities.AsNoTracking()
            .Where(link => recordingIds.Contains(link.CanonicalRecordingId)).Select(link => link.Id).ToArrayAsync(token);
        var decidedSnapshots = recordingIds.Length == 0 ? [] : await db.TrackMatches.AsNoTracking()
            .Where(match => match.OwnerUserId == owner && match.CanonicalRecordingId.HasValue && recordingIds.Contains(match.CanonicalRecordingId.Value))
            .Select(match => match.ExternalSnapshotId).Distinct().ToArrayAsync(token);
        IQueryable<ExternalMetadataSnapshotRecord> Visible(IQueryable<ExternalMetadataSnapshotRecord> query) => query.Where(item => item.OwnerUserId == owner &&
            item.Protocol == protocol && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId &&
            (item.ResourceKind == "track" || item.ResourceKind == "playback-metadata") &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == owner))));
        // A song has many snapshots, mostly of plays, and only the chosen one's payload is read, so payloads load later.
        var found = new List<ExternalMetadataSnapshotRecord>();
        if (hashes.Length > 0)
            found.AddRange(await Visible(db.ExternalMetadataSnapshots.AsNoTracking().Where(item => hashes.Contains(item.ExternalIdHash))).Select(WithoutPayload).ToArrayAsync(token));
        if (linkedIdentities.Length > 0)
            found.AddRange(await Visible(db.ExternalMetadataSnapshots.AsNoTracking()
                .Where(item => item.ProviderTrackIdentityId.HasValue && linkedIdentities.Contains(item.ProviderTrackIdentityId.Value))).Select(WithoutPayload).ToArrayAsync(token));
        if (decidedSnapshots.Length > 0)
            found.AddRange(await Visible(db.ExternalMetadataSnapshots.AsNoTracking().Where(item => decidedSnapshots.Contains(item.Id))).Select(WithoutPayload).ToArrayAsync(token));
        var snapshots = found.DistinctBy(item => item.Id).ToArray();
        Step("snapshots");
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        // Facts read only these few fields; a decision's candidate list is by far its largest column.
        var decisions = snapshotIds.Length == 0 ? [] : await db.TrackMatches.AsNoTracking().Where(item => item.OwnerUserId == owner && snapshotIds.Contains(item.ExternalSnapshotId))
            .Select(item => new TrackMatchRecord
            {
                Id = item.Id,
                OwnerUserId = item.OwnerUserId,
                ExternalSnapshotId = item.ExternalSnapshotId,
                CanonicalRecordingId = item.CanonicalRecordingId,
                LibraryTrackId = item.LibraryTrackId,
                State = item.State,
                Confidence = item.Confidence,
                DecisionVersion = item.DecisionVersion,
                DecidedAt = item.DecidedAt
            })
            .ToArrayAsync(token);
        recordingIds = recordingIds.Concat(decisions.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)).Distinct().ToArray();
        Step("decisions");
        var libraryTrackIds = library.Select(item => item.Id).ToArray();
        var playQuery = ListeningStatsService.CountedPlays(db, new(owner, protocol, execution.BackendInstanceId));
        var playCounts = await playQuery.Where(item =>
                item.CanonicalRecordingId != null && recordingIds.Contains(item.CanonicalRecordingId.Value) ||
                item.LibraryTrackId != null && libraryTrackIds.Contains(item.LibraryTrackId.Value) ||
                requested.Contains(item.TrackReference) || nativeLookupIds.Contains(item.TrackReference))
            .GroupBy(item => new { item.CanonicalRecordingId, item.LibraryTrackId, item.TrackReference })
            .Select(group => new { group.Key, Count = group.LongCount(), Last = group.Max(item => item.ListenedAt ?? item.StartedAt) })
            .ToArrayAsync(token);
        Step("plays");
        var links = recordingIds.Length == 0 ? [] : await db.ProviderTrackIdentities.AsNoTracking().Where(item => recordingIds.Contains(item.CanonicalRecordingId) &&
            item.ResourceKind == ProviderResourceKind.Track && item.Verification == ProviderIdentityVerification.Verified &&
            item.VerificationMethod != "automatic-suggestion" && item.VerificationMethod != "source-snapshot-hash" &&
            item.VerificationMethod != CatalogIdentityPolicy.SupersededVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod &&
            (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == owner))))
            .ToArrayAsync(token);
        Step("links");
        var layers = ManualTrackOverrides.Index(snapshots, await ManualTrackOverrides.LoadAsync(db, owner, snapshots, token), owner);
        var recordings = recordingIds.Length == 0 ? new Dictionary<Guid, CanonicalRecordingRecord>() :
            await db.CanonicalRecordings.AsNoTracking().Where(item => recordingIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        var identifiers = recordingIds.Length == 0 ? [] : await db.RecordingIdentifiers.AsNoTracking().Where(item => recordingIds.Contains(item.RecordingId) && item.Kind == RecordingIdentifierKinds.Isrc)
            .ToArrayAsync(token);
        Step("stored records");
        IReadOnlyList<FavoriteViewItem> view = external.Length == 0 ? [] : await favorites.ReadForRequestAsync(http);
        Step("favourites");
        var saved = view.Select(item => item.ItemId).ToHashSet(FavoriteViewReader.ItemComparer(view));
        var states = await (from state in db.FavoriteStates.AsNoTracking()
                            join accepted in db.FavoriteEvents on state.LastEventId equals accepted.Id
                            where state.OwnerUserId == owner && state.Protocol == protocol && state.BackendInstanceId == execution.BackendInstanceId &&
                                requested.Contains(state.ItemId) && accepted.OwnerUserId == owner && accepted.Protocol == state.Protocol &&
                                accepted.BackendInstanceId == state.BackendInstanceId && accepted.ItemId == state.ItemId &&
                                accepted.BackendPrincipalId == execution.VerifiedBackendPrincipalId &&
                                accepted.Operation == (state.IsFavorite ? FavoriteOperation.Favorite : FavoriteOperation.Unfavorite)
                            select state).ToArrayAsync(token);
        var eventIds = states.Select(item => item.LastEventId).ToArray();
        var actions = eventIds.Length == 0 ? [] : await (from action in db.FavoriteActions.AsNoTracking()
                                                         join accepted in db.FavoriteEvents.AsNoTracking() on action.EventId equals accepted.Id
                                                         where action.OwnerUserId == owner && accepted.OwnerUserId == owner && accepted.Protocol == protocol &&
                                                             accepted.BackendInstanceId == execution.BackendInstanceId && accepted.BackendPrincipalId == execution.VerifiedBackendPrincipalId &&
                                                             accepted.Operation == FavoriteOperation.Favorite && eventIds.Contains(accepted.Id) && action.ActionType == "download"
                                                         select action).ToArrayAsync(token);
        var heartKeys = states.Where(item => item.IsFavorite).Select(item => $"heart:{item.Id:N}").ToArray();
        var retained = recordingIds.Length == 0 ? [] : await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                                                              join recording in db.CanonicalRecordings on route.CanonicalRecordingId equals recording.Id
                                                              join file in db.ManagedFiles on route.ManagedFileId equals file.Id
                                                              join artifact in db.ProviderDownloadArtifacts on route.SourceArtifactId equals artifact.Id
                                                              where (recordingIds.Contains(recording.Id) || recording.MergedIntoId.HasValue && recordingIds.Contains(recording.MergedIntoId.Value)) &&
                                                                  file.IsManaged && file.RemovedAt == null && file.OwnerUserId == null && file.SourceJobId == null && file.Length > 0
                                                              select new { RecordingId = recording.MergedIntoId ?? recording.Id, File = file, Artifact = artifact }).ToArrayAsync(token);
        Step("favourite states");
        var placement = http.RequestServices.GetRequiredService<FilePlacementService>();
        retained = retained.Where(item => AvailableFile(placement, item.File, token)).ToArray();
        var fileIds = retained.Select(item => item.File.Id).ToArray();
        var references = fileIds.Length == 0 || heartKeys.Length == 0 ? [] : await db.ManagedFileReferences.AsNoTracking().Where(item => fileIds.Contains(item.ManagedFileId) &&
            item.ReleasedAt == null && heartKeys.Contains(item.ReferenceKey)).ToArrayAsync(token);
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(item => item.Enabled &&
            (item.OwnerUserId == owner || item.OwnerUserId == null) &&
            (item.SecretReferenceId == null || db.SecretReferences.Any(secret => secret.Id == item.SecretReferenceId && secret.RevokedAt == null)))
            .ToArrayAsync(token);
        Step("kept files");
        var songs = new Dictionary<string, Song>(StringComparer.Ordinal);
        var selectedSnapshots = new Dictionary<string, ExternalMetadataSnapshotRecord>(StringComparer.Ordinal);
        ExternalMetadataSnapshotRecord? Choose(string id, string provider, string externalId)
        {
            var recording = resolved.GetValueOrDefault(id);
            var candidates = provider == "allstarr" && recording != null
                ? snapshots.Where(snapshot => links.Any(link => link.CanonicalRecordingId == recording.Id && link.ProviderId == snapshot.ProviderId && link.ExternalIdHash == snapshot.ExternalIdHash) ||
                    decisions.Any(match => match.ExternalSnapshotId == snapshot.Id && match.CanonicalRecordingId == recording.Id))
                : snapshots.Where(snapshot => registry.ResolveProviderId(snapshot.ProviderId) == provider && snapshot.ExternalIdHash == CanonicalCatalogKeys.Hash(externalId));
            return candidates.OrderByDescending(item => item.ResourceKind == "track").ThenByDescending(item => item.RetrievedAt)
                .ThenByDescending(item => item.SnapshotVersion).FirstOrDefault();
        }
        var chosen = external.Where(source => !native.ContainsKey(source.Id)).Select(source => Choose(source.Id, source.Provider, source.ExternalId))
            .OfType<ExternalMetadataSnapshotRecord>().Select(item => item.Id).Distinct().ToArray();
        var payloads = chosen.Length == 0 ? new Dictionary<Guid, string>() : await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => chosen.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.PayloadJson, token);
        foreach (var source in external)
        {
            if (native.ContainsKey(source.Id)) continue;
            var recording = resolved.GetValueOrDefault(source.Id);
            var snapshot = Choose(source.Id, source.Provider, source.ExternalId);
            Song? song = null;
            if (snapshot != null)
            {
                var externalId = source.Provider == "allstarr" ? links.FirstOrDefault(link => link.ProviderId == snapshot.ProviderId && link.ExternalIdHash == snapshot.ExternalIdHash)?.ExternalId ?? "" : source.ExternalId;
                try { song = TrackIdentityService.ReadDisplaySong(payloads.GetValueOrDefault(snapshot.Id) ?? "{}", snapshot.ProviderId, externalId); }
                catch (JsonException) { }
            }
            if (source.Provider != "allstarr") song ??= await ReadCachedSongAsync(execution.RequireActor(), source.Provider, source.ExternalId, accounts, token);
            else if (song == null && recording != null)
                foreach (var link in links.Where(item => item.CanonicalRecordingId == recording.Id && policy.IsEnabled(item.ProviderId)))
                {
                    song = await ReadCachedSongAsync(execution.RequireActor(), link.ProviderId, link.ExternalId, accounts, token);
                    if (song != null) break;
                }
            song ??= observedSongs?.GetValueOrDefault(source.Id);
            if (song == null || source.Provider != "allstarr" && !policy.IsEnabled(song.ExternalProvider!) || !policy.Includes(song)) continue;
            song.Id = source.Id;
            song.CanonicalRecordingId = recording?.Id ?? decisions.Where(item => item.ExternalSnapshotId == snapshot?.Id)
                .OrderByDescending(item => item.DecisionVersion).ThenByDescending(item => item.DecidedAt).FirstOrDefault()?.CanonicalRecordingId;
            song.Isrc = recording?.Isrc ?? song.Isrc;
            // Catalog metadata may advertise a format; only measured facts populate audio.
            song.Codec = null;
            song.BitDepth = null;
            song.SampleRate = null;
            song.Bitrate = null;
            songs[source.Id] = song;
            if (snapshot != null) selectedSnapshots[source.Id] = snapshot;
        }
        Step("songs");
        if (identities is ITrackMediaFactsStore mediaFacts)
            await mediaFacts.ApplyStoredMediaFactsAsync(execution.RequireActor(), songs.Values.ToArray(), token);
        Step("media facts");
        // Each song's playback route is planned on its own, so a playlist's songs are planned a few at a time.
        using var routeGate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        var servingProviders = (await Task.WhenAll(external.Where(source => resolveServingProvider && source.Provider == "allstarr" && songs.ContainsKey(source.Id))
            .Select(async source =>
            {
                using (RequestTiming.Current?.Stage("lock")) await routeGate.WaitAsync(token);
                try { return (source.Id, Provider: gateway == null ? null : await gateway.GetStreamingProviderAsync(execution, source.Provider, source.ExternalId)); }
                finally { routeGate.Release(); }
            }))).ToDictionary(item => item.Id, item => item.Provider, StringComparer.Ordinal);
        Step("serving routes");
        var providers = new Dictionary<string, ItemDetailsProvider?>(StringComparer.Ordinal);
        var providerIds = songs.Values.Select(item => item.ExternalProvider!).Concat(servingProviders.Values.OfType<string>()).Distinct(StringComparer.Ordinal).ToArray();
        var icons = await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, providerIds, token);
        foreach (var providerId in providerIds)
        {
            providers[providerId] = registry.TryGet(providerId, out var descriptor)
                ? icons.GetValueOrDefault(providerId)?.Provider(providerId, descriptor!.DisplayName) ?? new(providerId, descriptor!.DisplayName, null) : null;
        }
        var choices = recordingIds.Length == 0 ? [] : await db.RecordingLyricsChoices.AsNoTracking().Where(item => recordingIds.Contains(item.CanonicalRecordingId) &&
            item.RevokedAt == null && (item.OwnerUserId == owner || item.OwnerUserId == null)).ToArrayAsync(token);
        var lyricsEndpoint = (await settings.GetAsync("LyricsPlus:BaseUrl", token)).NormalizedValue;
        var retainedLyrics = refreshStore == null ? new Dictionary<Guid, allstarr.Models.Lyrics.LyricsInfo>() :
            await refreshStore.RetainedAsync(execution, recordingIds);
        Step("lyrics choices");
        var libraryProvider = native.Count > 0 ? await http.RequestServices.GetRequiredService<LibraryProviderResolver>().ResolveAsync(http, execution) : null;
        // Each song's match and lyrics reads stand alone, so a batch reads a few songs at once instead of one after another.
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        var libraryMemo = new TrackMatchLibraryMemo();
        if (snapshots.Length > 0) await matches.PrepareSummariesAsync(http, execution, requested.Select(id => (Id: id,
            Recording: resolved.GetValueOrDefault(id)?.Id ?? library.FirstOrDefault(item => SameNativeId(item.BackendItemId, localIds.GetValueOrDefault(id) ?? id))?.CanonicalRecordingId))
            .SelectMany(item => item.Recording.HasValue ? new[] { item, (Id: CanonicalRecordingRecord.DefaultPublicId(item.Recording.Value), item.Recording) } : new[] { item })
            .DistinctBy(item => item.Id).ToDictionary(item => item.Id, item => item.Recording, StringComparer.Ordinal), libraryMemo);
        var matchTiming = new ItemMatchTiming();
        http.Items[typeof(ItemMatchTiming)] = matchTiming;
        // Each song finds its snapshot and decision through these instead of scanning every snapshot against every
        // decision and link, which cost about ten milliseconds a song on a mix.
        var snapshotOrder = snapshots.Select((item, index) => (item.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var snapshotsById = snapshots.ToDictionary(item => item.Id);
        var snapshotsByKey = snapshots.ToLookup(item => (item.ProviderId, item.ExternalIdHash));
        var snapshotsDecidedFor = decisions.Where(item => item.CanonicalRecordingId.HasValue)
            .ToLookup(item => item.CanonicalRecordingId!.Value, item => item.ExternalSnapshotId);
        var linkedKeys = links.ToLookup(link => link.CanonicalRecordingId, link => (link.ProviderId, link.ExternalIdHash));
        var decisionsBySnapshot = decisions.ToLookup(item => item.ExternalSnapshotId);
        Step("states and choices");
        var prepared = System.Diagnostics.Stopwatch.GetTimestamp();
        var built = await Task.WhenAll(requested.Select(async id =>
        {
            using (RequestTiming.Current?.Stage("lock")) await gate.WaitAsync(token);
            var itemStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return (Id: id, Details: await ReadItemAsync(id)); }
            finally
            {
                Interlocked.Add(ref itemTicks, System.Diagnostics.Stopwatch.GetElapsedTime(itemStarted).Ticks);
                gate.Release();
            }
        }));
        var items = built.Where(item => item.Details != null).ToDictionary(item => item.Id, item => item.Details!, StringComparer.Ordinal);
        logger?.LogInformation("Item details timing: prepare {Prepare} ms ({PrepareSteps} ms), songs {Songs} ms, matches {Matches} ms ({MatchSteps} ms), lyrics {Lyrics} ms, lookups {Lookups} ms, files {Files} ms and counts {Counts} ms of {Items} ms summed over {Count} songs",
            (int)System.Diagnostics.Stopwatch.GetElapsedTime(started, prepared).TotalMilliseconds, string.Join(", ", steps),
            (int)System.Diagnostics.Stopwatch.GetElapsedTime(prepared).TotalMilliseconds,
            (int)TimeSpan.FromTicks(Interlocked.Read(ref matchTicks)).TotalMilliseconds, matchTiming,
            (int)TimeSpan.FromTicks(Interlocked.Read(ref lyricsTicks)).TotalMilliseconds,
            (int)TimeSpan.FromTicks(Interlocked.Read(ref lookupTicks)).TotalMilliseconds,
            (int)TimeSpan.FromTicks(Interlocked.Read(ref fileTicks)).TotalMilliseconds,
            (int)TimeSpan.FromTicks(Interlocked.Read(ref countTicks)).TotalMilliseconds,
            (int)TimeSpan.FromTicks(Interlocked.Read(ref itemTicks)).TotalMilliseconds, requested.Length);
        using var serialization = RequestTiming.Current?.Stage("serialize");
        return (new(ids.Where(items.ContainsKey).Select(id => items[id]).ToArray(), ids.Where(id => !items.ContainsKey(id)).ToArray()), songs);

        async Task<ItemDetails?> ReadItemAsync(string id)
        {
            using var matching = RequestTiming.Current?.Stage("match");
            var lookupStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var song = songs.GetValueOrDefault(id);
            if (song == null && !native.ContainsKey(id)) return null;
            var local = library.FirstOrDefault(item => SameNativeId(item.BackendItemId, localIds.GetValueOrDefault(id) ?? id));
            var identity = song == null && local?.CanonicalRecordingId is { } localRecording
                ? resolved.GetValueOrDefault(localRecording.ToString("D")) : resolved.GetValueOrDefault(id);
            var recordingId = identity?.Id ?? song?.CanonicalRecordingId;
            var snapshot = selectedSnapshots.GetValueOrDefault(id);
            if (snapshot == null && recordingId is { } wanted)
                snapshot = snapshotsDecidedFor[wanted].Select(snapshotsById.GetValueOrDefault).OfType<ExternalMetadataSnapshotRecord>()
                    .Concat(linkedKeys[wanted].SelectMany(key => snapshotsByKey[key])).DistinctBy(item => item.Id)
                    .OrderByDescending(item => item.ResourceKind == "track").ThenByDescending(item => item.RetrievedAt)
                    .ThenByDescending(item => item.SnapshotVersion).ThenBy(item => snapshotOrder[item.Id]).FirstOrDefault();
            var decision = snapshot == null ? null : decisionsBySnapshot[snapshot.Id]
                .OrderByDescending(item => item.DecisionVersion).ThenByDescending(item => item.DecidedAt).FirstOrDefault();
            var authority = snapshot == null ? null : layers[snapshot.Id];
            Interlocked.Add(ref lookupTicks, System.Diagnostics.Stopwatch.GetElapsedTime(lookupStarted).Ticks);
            var match = snapshot == null || decision == null && authority?.Effective == null ? null : new ItemDetailsMatch(
                authority?.Effective is { } effective ? effective.Decision == ManualOverrideDecision.Pin ? "pinned" : "rejected" : decision!.State.ToString().ToLowerInvariant(),
                decision != null && double.IsFinite(decision.Confidence) ? Math.Clamp(decision.Confidence, 0, 1) : null, authority?.EffectiveScope ?? "automatic");
            if (snapshot != null)
            {
                var matchId = local != null && recordingId.HasValue ? CanonicalRecordingRecord.DefaultPublicId(recordingId.Value) : id;
                var matchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var effectiveMatch = await matches.ReadSummaryAsync(http, execution, matchId, libraryMemo);
                Interlocked.Add(ref matchTicks, System.Diagnostics.Stopwatch.GetElapsedTime(matchStarted).Ticks);
                if (effectiveMatch != null) match = new(effectiveMatch.State, effectiveMatch.Confidence, effectiveMatch.Authority);
            }
            var filesStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var state = states.FirstOrDefault(item => item.ItemId == id && item.IsFavorite);
            var storedFile = retained.FirstOrDefault(item => item.RecordingId == recordingId && File.Exists(item.File.CanonicalPath));
            var kept = state == null ? null : retained.FirstOrDefault(item => item.RecordingId == recordingId &&
                references.Any(reference => reference.ManagedFileId == item.File.Id && reference.ReferenceKey == $"heart:{state.Id:N}") && File.Exists(item.File.CanonicalPath));
            var action = state == null ? null : actions.FirstOrDefault(item => item.EventId == state.LastEventId);
            var keep = kept != null ? new ItemDetailsKeep("kept", null) : new ItemDetailsKeep(action?.State switch
            { FavoriteActionState.Pending or FavoriteActionState.Running => "inProgress", FavoriteActionState.Failed => "failed", _ => "none" }, null);
            Interlocked.Add(ref fileTicks, System.Diagnostics.Stopwatch.GetElapsedTime(filesStarted).Ticks);
            var choice = choices.Where(item => item.CanonicalRecordingId == recordingId).OrderByDescending(item => item.OwnerUserId == owner).FirstOrDefault();
            var lyricsSong = song ?? (local == null ? null : new Song
            {
                Id = id,
                Title = local.Title,
                Artist = local.Artist,
                Artists = [local.Artist],
                Album = local.Album ?? "",
                Isrc = local.Isrc,
                PreciseDuration = local.DurationMilliseconds is > 0 ? TimeSpan.FromMilliseconds(local.DurationMilliseconds.Value) : null,
                Duration = local.DurationMilliseconds is > 0 ? (int)(local.DurationMilliseconds.Value / 1000) : null,
                IsLocal = true,
                ExplicitContentLyrics = native[id].Rating
            });
            var lyricsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var lyrics = await StoredItemLyrics.ReadAsync(cache, lyricsSong, recordingId, choice?.SourceId, storedFile?.File.CanonicalPath,
                policy, lyricsEndpoint, execution, accounts, links, token,
                recordingDurationMs: recordingId.HasValue ? recordings.GetValueOrDefault(recordingId.Value)?.DurationMilliseconds : null,
                automaticLyrics: choice == null && recordingId.HasValue ? retainedLyrics.GetValueOrDefault(recordingId.Value) : null);
            Interlocked.Add(ref lyricsTicks, System.Diagnostics.Stopwatch.GetElapsedTime(lyricsStarted).Ticks);
            var audio = song == null || song.Codec == null && song.BitDepth == null && song.SampleRate == null && song.Bitrate == null ? null :
                new ItemDetailsAudio(song.Codec, song.BitDepth, song.SampleRate, song.Bitrate);
            if (audio == null && kept?.Artifact is { } artifact &&
                (artifact.Codec != null || artifact.BitDepth != null || artifact.SampleRate != null || artifact.Bitrate != null))
                audio = new(artifact.Codec, artifact.BitDepth, artifact.SampleRate, artifact.Bitrate);
            var explicitContent = (song?.ExplicitContentLyrics ?? native.GetValueOrDefault(id)?.Rating) switch { 1 => true, 0 or 3 => false, _ => (bool?)null };
            explicitContent ??= recordingId.HasValue ? recordings.GetValueOrDefault(recordingId.Value)?.IsExplicit : null;
            var countStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var counted = playCounts.Where(item => recordingId.HasValue && item.Key.CanonicalRecordingId == recordingId ||
                local != null && item.Key.LibraryTrackId == local.Id || item.Key.TrackReference == id ||
                SameNativeId(item.Key.TrackReference, localIds.GetValueOrDefault(id) ?? id)).ToArray();
            Interlocked.Add(ref countTicks, System.Diagnostics.Stopwatch.GetElapsedTime(countStarted).Ticks);
            var isSong = song != null || local != null || native.GetValueOrDefault(id)?.IsSong == true;
            return new ItemDetails(id, song != null, recordingId?.ToString("D"), identity?.Isrc ?? song?.Isrc ?? local?.Isrc ?? identifiers.FirstOrDefault(item => item.RecordingId == recordingId)?.Value,
                song == null ? libraryProvider : providers.GetValueOrDefault(servingProviders.GetValueOrDefault(id) ?? song.ExternalProvider!), audio, explicitContent,
                song == null ? native[id].Saved : saved.Contains(id), keep, match, lyrics)
            {
                PlayCount = isSong ? counted.Sum(item => item.Count) : null,
                LastPlayedAt = isSong ? counted.Select(item => item.Last).Max() : null
            };
        }
    }

    private async Task<Dictionary<string, NativeDetails>> ReadNativeAsync(HttpContext http, ProtocolExecutionContext execution, string[] ids,
        EffectiveProviderPolicySnapshot policy)
    {
        var result = new Dictionary<string, NativeDetails>(StringComparer.Ordinal);
        if (ids.Length == 0) return result;
        foreach (var batch in ids.GroupBy(id => Guid.TryParse(id, out _)))
        {
            var (body, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items",
                new() { ["ids"] = string.Join(',', batch), ["limit"] = "100", ["enableUserData"] = "true" }, http.Request.Headers, execution.CancellationToken);
            using var responseBody = body;
            if (status is 400 or 401 or 403 or 404) continue;
            if (status != 200 || body == null) throw new HttpRequestException("Library details are unavailable.");
            if (!body.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("Id", out var value) || value.GetString() is not { } id) continue;
                var rating = item.TryGetProperty("OfficialRating", out var field) && field.ValueKind == JsonValueKind.String
                    ? CleanOnlyProtocolContent.Rating(field.GetString()) : null;
                if (policy.Preferences.ExplicitFilter == "CleanOnly" && rating is not (0 or 3)) continue;
                foreach (var requestedId in batch.Where(requestedId => SameNativeId(id, requestedId)))
                    result[requestedId] = new(item.TryGetProperty("UserData", out var data) && data.TryGetProperty("IsFavorite", out var favorite) && favorite.ValueKind == JsonValueKind.True, rating,
                        item.TryGetProperty("Type", out var type) && type.GetString() == "Audio");
            }
        }
        return result;
    }

    private sealed record Source(string Id, string Provider, string ExternalId);
    private sealed record NativeDetails(bool Saved, int? Rating, bool IsSong);

    private static bool SameNativeId(string first, string second) => first == second ||
        Guid.TryParse(first, out var firstId) && Guid.TryParse(second, out var secondId) && firstId == secondId;

    private async Task<Song?> ReadCachedSongAsync(ProviderActorContext actor, string provider, string externalId,
        IReadOnlyList<ProviderAccountRecord> accounts, CancellationToken token)
    {
        var contexts = accounts.Where(item => item.ProviderId == provider).Select(item => new ProviderAccountContext(
            item.Id, item.ProviderId, item.Scope, item.Revision, ownerUserId: item.OwnerUserId, secretReferenceId: item.SecretReferenceId));
        foreach (var account in contexts.Cast<ProviderAccountContext?>().Prepend(null))
            foreach (var detail in new[] { true, false })
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var payload = await cache.GetStringAsync(ProtocolProviderGateway.MetadataCacheKey(actor, provider,
                        ProviderResourceKind.Track, externalId, detail, account));
                    if (payload == null || TrackIdentityService.ReadDisplaySong(payload, provider, externalId) is not { } song) continue;
                    return song;
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
        return null;
    }

    private static bool ValidProvider(string provider)
    {
        try { ProviderContractValidation.ProviderId(provider, nameof(provider)); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool AvailableFile(FilePlacementService placement, ManagedFileOwnershipEntity file, CancellationToken token)
    {
        try { placement.VerifyOwnedFileIdentity(EfManagedFileOwnershipStore.Map(file)!, token); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
