using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
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
    IProtocolProviderGateway? gateway = null, ILibraryIndexService? libraryIndex = null)
{
    public async Task<ItemDetailsResponse> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string[] ids)
    {
        var token = execution.CancellationToken;
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var protocol = execution.Protocol.ToString().ToLowerInvariant();
        var requested = ids.Distinct(StringComparer.Ordinal).ToArray();
        var external = requested.Where(id => id.Length <= 500).Select(id => (Id: id, Parsed: LocalLibraryService.ParseExternalResource(id)))
            .Where(item => item.Parsed.isExternal && item.Parsed.type == "song" && ValidProvider(item.Parsed.provider!))
            .Select(item => new Source(item.Id, registry.ResolveProviderId(item.Parsed.provider!), item.Parsed.externalId!)).ToArray();
        IReadOnlyDictionary<string, string> localIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (libraryIndex != null && external.Length > 0)
        {
            var known = await identities.ResolveRecordingsAsync(execution.RequireActor(), external.Select(source => source.Id).ToArray(), token);
            var lookups = external.Where(source => known.GetValueOrDefault(source.Id) != null).Select(source => new LibraryRecordingLookup(
                source.Id, known[source.Id]!.Id, known[source.Id]!.Isrc, source.Provider, source.ExternalId)).ToArray();
            if (lookups.Length > 0) localIds = await libraryIndex.FindKnownRecordingTracksAsync(execution, lookups, token);
        }
        var nativeIds = requested.Where(id => !id.StartsWith("ext-", StringComparison.Ordinal) &&
            id.Length is > 0 and <= 500 && !id.Contains(',')).Concat(localIds.Values).Distinct(StringComparer.Ordinal).ToArray();
        var native = await ReadNativeAsync(http, execution, nativeIds);
        foreach (var pair in localIds)
            if (native.TryGetValue(pair.Value, out var nativeSaved)) native[pair.Key] = nativeSaved;
        var nativeLookupIds = nativeIds.SelectMany(id => Guid.TryParse(id, out var parsed)
            ? new[] { id, parsed.ToString("N"), parsed.ToString("D") } : [id]).Distinct(StringComparer.Ordinal).ToArray();
        await using var db = await factory.CreateDbContextAsync(token);
        var library = await db.LibraryTracks.AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.Protocol == protocol && item.BackendInstanceId == execution.BackendInstanceId && nativeLookupIds.Contains(item.BackendItemId))
            .ToArrayAsync(token);
        var resolved = await identities.ResolveRecordingsAsync(execution.RequireActor(), external.Select(item => item.Id)
            .Concat(library.Where(item => item.CanonicalRecordingId.HasValue).Select(item => item.CanonicalRecordingId!.Value.ToString("D"))).ToArray(), token);
        var recordingIds = resolved.Values.OfType<CanonicalRecordingIdentity>().Select(item => item.Id).Distinct().ToArray();
        var hashes = external.Select(item => CanonicalCatalogKeys.Hash(item.ExternalId)).Distinct().ToArray();
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.Protocol == protocol && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId &&
            (item.ResourceKind == "track" || item.ResourceKind == "playback-metadata") &&
            (hashes.Contains(item.ExternalIdHash) || db.ProviderTrackIdentities.Any(link => link.Id == item.ProviderTrackIdentityId &&
                recordingIds.Contains(link.CanonicalRecordingId)) || db.TrackMatches.Any(match => match.OwnerUserId == owner &&
                match.ExternalSnapshotId == item.Id && match.CanonicalRecordingId.HasValue && recordingIds.Contains(match.CanonicalRecordingId.Value))) &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == owner))))
            .ToArrayAsync(token);
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisions = await db.TrackMatches.AsNoTracking().Where(item => item.OwnerUserId == owner && snapshotIds.Contains(item.ExternalSnapshotId))
            .ToArrayAsync(token);
        recordingIds = recordingIds.Concat(decisions.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)).Distinct().ToArray();
        var links = await db.ProviderTrackIdentities.AsNoTracking().Where(item => recordingIds.Contains(item.CanonicalRecordingId) &&
            item.ResourceKind == ProviderResourceKind.Track && item.Verification == ProviderIdentityVerification.Verified &&
            item.VerificationMethod != "automatic-suggestion" && item.VerificationMethod != "source-snapshot-hash" &&
            item.VerificationMethod != CatalogIdentityPolicy.SupersededVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod &&
            (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == owner))))
            .ToArrayAsync(token);
        var layers = ManualTrackOverrides.Index(snapshots, await ManualTrackOverrides.LoadAsync(db, owner, snapshots, token), owner);
        var recordings = await db.CanonicalRecordings.AsNoTracking().Where(item => recordingIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        var identifiers = await db.RecordingIdentifiers.AsNoTracking().Where(item => recordingIds.Contains(item.RecordingId) && item.Kind == RecordingIdentifierKinds.Isrc)
            .ToArrayAsync(token);
        var policy = execution.Policy ?? await policies.ResolveForUserAsync(owner, token);
        var view = await favorites.ReadForRequestAsync(http);
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
        var actions = await (from action in db.FavoriteActions.AsNoTracking()
                             join accepted in db.FavoriteEvents.AsNoTracking() on action.EventId equals accepted.Id
                             where action.OwnerUserId == owner && accepted.OwnerUserId == owner && accepted.Protocol == protocol &&
                                 accepted.BackendInstanceId == execution.BackendInstanceId && accepted.BackendPrincipalId == execution.VerifiedBackendPrincipalId &&
                                 accepted.Operation == FavoriteOperation.Favorite && eventIds.Contains(accepted.Id) && action.ActionType == "download"
                             select action).ToArrayAsync(token);
        var heartKeys = states.Where(item => item.IsFavorite).Select(item => $"heart:{item.Id:N}").ToArray();
        var retained = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                              join recording in db.CanonicalRecordings on route.CanonicalRecordingId equals recording.Id
                              join file in db.ManagedFiles on route.ManagedFileId equals file.Id
                              join artifact in db.ProviderDownloadArtifacts on route.SourceArtifactId equals artifact.Id
                              where (recordingIds.Contains(recording.Id) || recording.MergedIntoId.HasValue && recordingIds.Contains(recording.MergedIntoId.Value)) &&
                                  file.IsManaged && file.RemovedAt == null && file.OwnerUserId == null && file.SourceJobId == null && file.Length > 0
                              select new { RecordingId = recording.MergedIntoId ?? recording.Id, File = file, Artifact = artifact }).ToArrayAsync(token);
        var placement = http.RequestServices.GetRequiredService<FilePlacementService>();
        retained = retained.Where(item => AvailableFile(placement, item.File, token)).ToArray();
        var fileIds = retained.Select(item => item.File.Id).ToArray();
        var references = await db.ManagedFileReferences.AsNoTracking().Where(item => fileIds.Contains(item.ManagedFileId) &&
            item.ReleasedAt == null && heartKeys.Contains(item.ReferenceKey)).ToArrayAsync(token);
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(item => item.Enabled &&
            (item.OwnerUserId == owner || item.OwnerUserId == null) &&
            (item.SecretReferenceId == null || db.SecretReferences.Any(secret => secret.Id == item.SecretReferenceId && secret.RevokedAt == null)))
            .ToArrayAsync(token);
        var songs = new Dictionary<string, Song>(StringComparer.Ordinal);
        var selectedSnapshots = new Dictionary<string, ExternalMetadataSnapshotRecord>(StringComparer.Ordinal);
        foreach (var source in external)
        {
            if (native.ContainsKey(source.Id)) continue;
            var recording = resolved.GetValueOrDefault(source.Id);
            var candidates = source.Provider == "allstarr" && recording != null
                ? snapshots.Where(snapshot => links.Any(link => link.CanonicalRecordingId == recording.Id && link.ProviderId == snapshot.ProviderId && link.ExternalIdHash == snapshot.ExternalIdHash) ||
                    decisions.Any(match => match.ExternalSnapshotId == snapshot.Id && match.CanonicalRecordingId == recording.Id))
                : snapshots.Where(snapshot => snapshot.ProviderId == source.Provider && snapshot.ExternalIdHash == CanonicalCatalogKeys.Hash(source.ExternalId));
            var snapshot = candidates.OrderByDescending(item => item.ResourceKind == "track").ThenByDescending(item => item.RetrievedAt)
                .ThenByDescending(item => item.SnapshotVersion).FirstOrDefault();
            Song? song = null;
            if (snapshot != null)
            {
                var externalId = source.Provider == "allstarr" ? links.FirstOrDefault(link => link.ProviderId == snapshot.ProviderId && link.ExternalIdHash == snapshot.ExternalIdHash)?.ExternalId ?? "" : source.ExternalId;
                try { song = TrackIdentityService.ReadDisplaySong(snapshot.PayloadJson, snapshot.ProviderId, externalId); }
                catch (JsonException) { }
            }
            if (source.Provider != "allstarr") song ??= await ReadCachedSongAsync(execution.RequireActor(), source.Provider, source.ExternalId, accounts, token);
            else if (song == null && recording != null)
                foreach (var link in links.Where(item => item.CanonicalRecordingId == recording.Id && policy.IsEnabled(item.ProviderId)))
                {
                    song = await ReadCachedSongAsync(execution.RequireActor(), link.ProviderId, link.ExternalId, accounts, token);
                    if (song != null) break;
                }
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
        if (identities is ITrackMediaFactsStore mediaFacts)
            await mediaFacts.ApplyStoredMediaFactsAsync(execution.RequireActor(), songs.Values.ToArray(), token);
        var servingProviders = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var source in external.Where(source => source.Provider == "allstarr" && songs.ContainsKey(source.Id)))
            servingProviders[source.Id] = gateway == null ? null : await gateway.GetStreamingProviderAsync(execution, source.Provider, source.ExternalId);
        var providers = new Dictionary<string, ItemDetailsProvider?>(StringComparer.Ordinal);
        var providerIds = songs.Values.Select(item => item.ExternalProvider!).Concat(servingProviders.Values.OfType<string>()).Distinct(StringComparer.Ordinal).ToArray();
        var icons = await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, providerIds, token);
        foreach (var providerId in providerIds)
        {
            providers[providerId] = registry.TryGet(providerId, out var descriptor)
                ? icons.GetValueOrDefault(providerId)?.Provider(providerId, descriptor!.DisplayName) ?? new(providerId, descriptor!.DisplayName, null) : null;
        }
        var choices = await db.RecordingLyricsChoices.AsNoTracking().Where(item => recordingIds.Contains(item.CanonicalRecordingId) &&
            item.RevokedAt == null && (item.OwnerUserId == owner || item.OwnerUserId == null)).ToArrayAsync(token);
        var lyricsEndpoint = (await settings.GetAsync("LyricsPlus:BaseUrl", token)).NormalizedValue;
        var libraryProvider = native.Count > 0 ? await http.RequestServices.GetRequiredService<LibraryProviderResolver>().ResolveAsync(http, execution) : null;
        var items = new Dictionary<string, ItemDetails>(StringComparer.Ordinal);
        foreach (var id in requested)
        {
            var song = songs.GetValueOrDefault(id);
            if (song == null && !native.ContainsKey(id)) continue;
            var local = library.FirstOrDefault(item => SameNativeId(item.BackendItemId, localIds.GetValueOrDefault(id) ?? id));
            var identity = song == null && local?.CanonicalRecordingId is { } localRecording
                ? resolved.GetValueOrDefault(localRecording.ToString("D")) : resolved.GetValueOrDefault(id);
            var recordingId = identity?.Id ?? song?.CanonicalRecordingId;
            var snapshot = selectedSnapshots.GetValueOrDefault(id);
            if (snapshot == null && recordingId.HasValue)
                snapshot = snapshots.Where(item => decisions.Any(match => match.ExternalSnapshotId == item.Id && match.CanonicalRecordingId == recordingId) ||
                        links.Any(link => link.CanonicalRecordingId == recordingId && link.ProviderId == item.ProviderId && link.ExternalIdHash == item.ExternalIdHash))
                    .OrderByDescending(item => item.ResourceKind == "track").ThenByDescending(item => item.RetrievedAt)
                    .ThenByDescending(item => item.SnapshotVersion).FirstOrDefault();
            var decision = snapshot == null ? null : decisions.Where(item => item.ExternalSnapshotId == snapshot.Id)
                .OrderByDescending(item => item.DecisionVersion).ThenByDescending(item => item.DecidedAt).FirstOrDefault();
            var authority = snapshot == null ? null : layers[snapshot.Id];
            var match = snapshot == null || decision == null && authority?.Effective == null ? null : new ItemDetailsMatch(
                authority?.Effective is { } effective ? effective.Decision == ManualOverrideDecision.Pin ? "pinned" : "rejected" : decision!.State.ToString().ToLowerInvariant(),
                decision != null && double.IsFinite(decision.Confidence) ? Math.Clamp(decision.Confidence, 0, 1) : null, authority?.EffectiveScope ?? "automatic");
            var state = states.FirstOrDefault(item => item.ItemId == id && item.IsFavorite);
            var storedFile = retained.FirstOrDefault(item => item.RecordingId == recordingId && File.Exists(item.File.CanonicalPath));
            var kept = state == null ? null : retained.FirstOrDefault(item => item.RecordingId == recordingId &&
                references.Any(reference => reference.ManagedFileId == item.File.Id && reference.ReferenceKey == $"heart:{state.Id:N}") && File.Exists(item.File.CanonicalPath));
            var action = state == null ? null : actions.FirstOrDefault(item => item.EventId == state.LastEventId);
            var keep = kept != null ? new ItemDetailsKeep("kept", null) : new ItemDetailsKeep(action?.State switch
            { FavoriteActionState.Pending or FavoriteActionState.Running => "inProgress", FavoriteActionState.Failed => "failed", _ => "none" }, null);
            var choice = choices.Where(item => item.CanonicalRecordingId == recordingId).OrderByDescending(item => item.OwnerUserId == owner).FirstOrDefault();
            var lyricsSong = song ?? (local == null ? null : new Song
            {
                Id = id,
                Title = local.Title,
                Artist = local.Artist,
                Artists = [local.Artist],
                Album = local.Album ?? "",
                Isrc = local.Isrc,
                IsLocal = true
            });
            var lyrics = await StoredItemLyrics.ReadAsync(cache, lyricsSong, recordingId, choice?.SourceId, storedFile?.File.CanonicalPath,
                policy, lyricsEndpoint, execution, accounts, links, token);
            var audio = song == null || song.Codec == null && song.BitDepth == null && song.SampleRate == null && song.Bitrate == null ? null :
                new ItemDetailsAudio(song.Codec, song.BitDepth, song.SampleRate, song.Bitrate);
            if (audio == null && kept?.Artifact is { } artifact &&
                (artifact.Codec != null || artifact.BitDepth != null || artifact.SampleRate != null || artifact.Bitrate != null))
                audio = new(artifact.Codec, artifact.BitDepth, artifact.SampleRate, artifact.Bitrate);
            var explicitContent = song?.ExplicitContentLyrics switch { 1 => true, 0 => false, _ => (bool?)null };
            explicitContent ??= recordingId.HasValue ? recordings.GetValueOrDefault(recordingId.Value)?.IsExplicit : null;
            items[id] = new(id, song != null, recordingId?.ToString("D"), identity?.Isrc ?? song?.Isrc ?? local?.Isrc ?? identifiers.FirstOrDefault(item => item.RecordingId == recordingId)?.Value,
                song == null ? libraryProvider : providers.GetValueOrDefault(servingProviders.ContainsKey(id) ? servingProviders[id] ?? "allstarr" : song.ExternalProvider!), audio, explicitContent,
                song == null ? native[id] : saved.Contains(id), keep, match, lyrics);
        }
        return new(ids.Where(items.ContainsKey).Select(id => items[id]).ToArray(), ids.Where(id => !items.ContainsKey(id)).ToArray());
    }

    private async Task<Dictionary<string, bool>> ReadNativeAsync(HttpContext http, ProtocolExecutionContext execution, string[] ids)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
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
                foreach (var requestedId in batch.Where(requestedId => SameNativeId(id, requestedId)))
                    result[requestedId] = item.TryGetProperty("UserData", out var data) && data.TryGetProperty("IsFavorite", out var favorite) && favorite.ValueKind == JsonValueKind.True;
            }
        }
        return result;
    }

    private sealed record Source(string Id, string Provider, string ExternalId);

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
