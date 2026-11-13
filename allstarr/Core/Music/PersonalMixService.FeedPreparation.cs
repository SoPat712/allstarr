using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientEvents;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    public const string FeedPrepareJobType = "mixes.prepare-service-feeds";
    private const string PreparedFeedKind = "service-feed-prepared";

    private static string? SavedFeedOwner(ProviderPlaylistSummary item, UserRecord user, ProviderAccountRecord account)
    {
        var wrapped = Regex.Match(item.Name, @"^(?<name>.+?)[’']s Top Songs \d{4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var person = wrapped.Success ? wrapped.Groups["name"].Value :
            PlaylistSourceKinds.IsService(item.Id.ProviderId, item.Owner.ProviderUserId) || item.Owner.DisplayName == item.Owner.ProviderUserId
                ? null : item.Owner.DisplayName;
        if (string.IsNullOrWhiteSpace(person)) return null;
        bool SameName(string name) => person.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            !person.Contains(' ') && person.Equals(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), StringComparison.OrdinalIgnoreCase);
        return SameName(user.DisplayName) || SameName(account.DisplayName) ? null : person;
    }

    private Task QueueServiceFeedPreparationAsync(Guid owner, ProviderAccountRecord account, int offset,
        DateTimeOffset generation, CancellationToken token) => jobQueue == null ? Task.CompletedTask :
        jobQueue.EnqueueAsync(new DurableJobEnqueueRequest<ServiceFeedPrepareRequest>(FeedPrepareJobType,
            $"service-feed-prepare:{account.Id:N}:{account.Revision}:{generation.UtcTicks}:{offset}", new(account.Id, offset),
            owner, Priority: -100, AvailableAt: generation, ProviderAccountId: account.Id, Capability: "playlist"), token);

    internal async Task PrepareServiceFeedsAsync(Guid owner, ServiceFeedPrepareRequest request, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.AccountId && item.OwnerUserId == owner && item.Enabled, token);
        if (account == null) return;
        var cards = (await ServiceFeedCardsAsync(user, token, queuePreparation: false)).Where(item => item.ProviderAccountId == account.Id)
            .OrderBy(item => item.SourcePlaylistId, StringComparer.Ordinal).ToArray();
        var feedKey = Hash(Scope(user) + ":" + account.Revision);
        var generation = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner && item.ProviderAccountId == account.Id &&
                item.ResourceKind == "service-feeds" && item.ExternalIdHash == feedKey).OrderByDescending(item => item.RetrievedAt).Select(item => item.RetrievedAt).FirstOrDefaultAsync(token);
        if (generation == default) return;
        await QueueServiceFeedPreparationAsync(owner, account, 0, generation + FeedWindow, token);
        var linked = await ExternalLinks(db, user).Where(item => item.Enabled && item.SourceProviderId == account.ProviderId &&
            (item.ProviderAccountId == account.Id || item.ProviderAccountId == null)).Select(item => item.SourcePlaylistId).ToArrayAsync(token);
        foreach (var card in cards.Skip(request.Offset).Take(3).Where(item => !linked.Contains(item.SourcePlaylistId)))
        {
            var key = Hash(Scope(user) + ":" + account.Revision + ":" + card.SourcePlaylistId);
            await using var preparation = await feedReads.AcquireAsync("songs:" + account.Id + ":" + key, token);
            var rows = db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == owner && item.ProviderAccountId == account.Id &&
                item.ResourceKind == PreparedFeedKind && item.ExternalIdHash == key);
            var saved = await rows.AsNoTracking().OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
            if (saved?.RetrievedAt >= generation) continue;
            PersonalMix prepared;
            try { prepared = await ServiceFeedSongsAsync(user, card, token); }
            catch (PlaylistSourceUnavailableException error) when (error.Error?.Kind == ProviderErrorKind.NotFound)
            { prepared = card with { Songs = [], Readiness = new(false, "Your service playlist is unavailable. We'll try again later.") }; }
            var json = JsonSerializer.Serialize(prepared, DocumentJson);
            var version = (saved?.SnapshotVersion ?? 0) + 1;
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                ProviderAccountId = account.Id,
                ProviderId = account.ProviderId,
                ResourceKind = PreparedFeedKind,
                ExternalIdHash = key,
                SnapshotVersion = version,
                Protocol = user.BackendType,
                BackendInstanceId = user.BackendInstanceId,
                BackendPrincipalId = user.BackendPrincipalId,
                ProviderRevision = account.Revision.ToString(CultureInfo.InvariantCulture),
                CorrelationId = "service-feeds",
                PayloadJson = json,
                PayloadSha256 = Hash(json),
                RetrievedAt = clock.UtcNow
            });
            await db.SaveChangesAsync(token);
            if (coverImages != null) WarmCovers(owner, [prepared]);
            await PlaylistSnapshotRetention.PruneDocumentsAsync(rows.Where(item => item.SnapshotVersion <= version - KeptDocuments), token);
            clientEvents?.Publish(owner, ClientEvent.Mix(card.Id));
        }
        if (request.Offset + 3 < cards.Length)
            await QueueServiceFeedPreparationAsync(owner, account, request.Offset + 3, generation, token);
    }

    private async Task<PersonalMix> PreparedServiceFeedAsync(UserRecord user, PersonalMix card, CancellationToken token) =>
        (await PreparedServiceFeedsAsync(user, [card], token))[card.Id];

    private async Task<IReadOnlyDictionary<Guid, PersonalMix>> PreparedServiceFeedsAsync(UserRecord user, PersonalMix[] cards,
        CancellationToken token)
    {
        var result = new Dictionary<Guid, PersonalMix>();
        if (cards.Length == 0) return result;
        await using var db = await factory.CreateDbContextAsync(token);
        var accounts = cards.Select(card => card.ProviderAccountId).OfType<Guid>().Distinct().ToArray();
        var revisions = await db.ProviderAccounts.AsNoTracking().Where(item => accounts.Contains(item.Id) && item.OwnerUserId == user.Id && item.Enabled)
            .ToDictionaryAsync(item => item.Id, item => item.Revision, token);
        var keys = cards.Where(card => card.ProviderAccountId is { } account && revisions.ContainsKey(account))
            .ToDictionary(card => card.Id, card => Hash(Scope(user) + ":" + revisions[card.ProviderAccountId!.Value] + ":" + card.SourcePlaylistId));
        var hashes = keys.Values.Distinct().ToArray();
        var snapshots = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user.Id &&
            accounts.Contains(item.ProviderAccountId!.Value) && item.ResourceKind == PreparedFeedKind && hashes.Contains(item.ExternalIdHash));
        var rows = await snapshots.Where(item => !snapshots.Any(newer => newer.ProviderAccountId == item.ProviderAccountId &&
            newer.ExternalIdHash == item.ExternalIdHash && newer.SnapshotVersion > item.SnapshotVersion)).ToArrayAsync(token);
        var latest = rows.GroupBy(item => (item.ProviderAccountId, item.ExternalIdHash))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.SnapshotVersion).First());
        var prepared = new List<(PersonalMix Card, ExternalMetadataSnapshotRecord Saved, PersonalMix Mix)>();
        foreach (var card in cards)
        {
            if (!keys.TryGetValue(card.Id, out var key))
                result[card.Id] = card with { Songs = [], Readiness = new(false, "Your service is unavailable.") };
            else if (!latest.TryGetValue((card.ProviderAccountId, key), out var saved))
                result[card.Id] = card with { Songs = [], Readiness = new(false, "Preparing songs from your service…") };
            else
                prepared.Add((card, saved, JsonSerializer.Deserialize<PersonalMix>(saved.PayloadJson)!));
        }
        if (prepared.Count == 0) return result;
        var context = await ContextAsync(user, token);
        var allSongs = prepared.SelectMany(item => item.Mix.Songs).Select(item => item.Song).ToArray();
        var blocked = await taste.BlockedSongsAsync(user.Id, allSongs, token);
        var localIds = allSongs.Where(song => song.IsLocal).Select(song => song.Id).Distinct().ToArray();
        var allowed = localIds.Length == 0 ? [] : await LibraryTrackAccess.Query(db, context, await libraryAccess.ResolveAsync(context, token))
            .Where(item => localIds.Contains(item.BackendItemId)).Select(item => item.BackendItemId).ToArrayAsync(token);
        foreach (var (card, saved, mix) in prepared)
        {
            var songs = mix.Songs.Where(item => !blocked.Contains(item.Song) && context.Policy!.Includes(item.Song) &&
                (!item.Song.IsLocal || allowed.Contains(item.Song.Id))).ToArray();
            result[card.Id] = card with
            {
                Songs = songs,
                UpdatedAt = saved.RetrievedAt,
                SongCount = mix.SongCount ?? card.SongCount,
                Evidence = mix.Evidence is { } evidence ? evidence with
                { FromMusic = songs.Count(item => item.Song.IsLocal), FromServices = songs.Count(item => !item.Song.IsLocal), Shown = songs.Length } : null,
                Readiness = new(songs.Length > 0, songs.Length > 0 ? "Ready" : mix.Songs.Count == 0 ? mix.Readiness.Message : "No playable songs right now.")
            };
        }
        return result;
    }

    private async Task<PersonalMix> MatchServiceFeedLibraryAsync(UserRecord user, PersonalMix mix, ProtocolExecutionContext context, CancellationToken token)
    {
        if (mix.Songs.All(item => item.Song.IsLocal) || !context.Policy!.IsAutomatic(mix.Source!)) return mix;
        await using var db = await factory.CreateDbContextAsync(token);
        var access = await libraryAccess.ResolveAsync(context, token);
        var library = await LibraryTrackAccess.Query(db, context, access).ToArrayAsync(token);
        var matcher = (feedMatcher ?? new TrackMatchDecisionEngine()).WithLocalPriorityWindow(context.Policy!.LocalPreferenceWindow,
            context.Policy.Preferences.ExplicitFilter == "CleanOnly" ? ProviderExplicitContentPolicy.CleanOnly : ProviderExplicitContentPolicy.Allow);
        var candidates = matcher.PrepareCandidates(library.Select(LocalCopyPreference.ToCandidate));
        var scope = new TrackMatchScope(user.Id, user.BackendInstanceId, mix.ProviderAccountId, 1, 1, access.LibraryIds.ToHashSet(StringComparer.Ordinal));
        foreach (var item in mix.Songs.Where(item => !item.Song.IsLocal))
        {
            var song = item.Song;
            var match = matcher.Decide(scope, new(song.Id, mix.Source!, song.ExternalId!, song.Title, song.Artist, song.Album, song.AlbumArtist,
                song.Duration * 1000L, song.Isrc, null, song.ExplicitContentLyrics is { } rating ? rating != 0 : null, song.CanonicalRecordingId), candidates);
            if (match.State is not (TrackMatchReviewState.Accepted or TrackMatchReviewState.Pinned) || match.SelectedBackendItemId == null) continue;
            song.Id = match.SelectedBackendItemId;
            song.IsLocal = true;
            song.ExternalProvider = null;
            song.ExternalId = null;
            song.AlbumId = null;
        }
        return mix;
    }
}

public sealed record ServiceFeedPrepareRequest(Guid AccountId, int Offset);

public sealed class ServiceFeedPreparationJobHandler(PersonalMixService mixes) : IDurableJobHandler
{
    public string JobType => PersonalMixService.FeedPrepareJobType;
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Claim.OwnerUserId is not { } owner) return DurableJobCompletion.Failure("mix-owner-missing", "A linked listener is required.");
        var request = context.Claim.Payload.Deserialize<ServiceFeedPrepareRequest>()!;
        if (request.AccountId != context.Claim.ProviderAccountId) return DurableJobCompletion.Failure("mix-account-mismatch", "The service connection changed.");
        try { await mixes.PrepareServiceFeedsAsync(owner, request, cancellationToken); return DurableJobCompletion.Success(); }
        catch (PlaylistSourceUnavailableException error)
        { return DurableJobCompletion.Retry("service-feed-unavailable", error.Message, error.Error?.RetryAfter ?? TimeSpan.FromMinutes(2)); }
    }
}
