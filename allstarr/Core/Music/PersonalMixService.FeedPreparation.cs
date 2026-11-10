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
            await rows.Where(item => item.SnapshotVersion <= version - KeptDocuments).ExecuteDeleteAsync(token);
            clientEvents?.Publish(owner, ClientEvent.Mix(card.Id));
        }
        if (request.Offset + 3 < cards.Length)
            await QueueServiceFeedPreparationAsync(owner, account, request.Offset + 3, generation, token);
    }

    private async Task<PersonalMix> PreparedServiceFeedAsync(UserRecord user, PersonalMix card, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var revision = await db.ProviderAccounts.AsNoTracking().Where(item => item.Id == card.ProviderAccountId && item.OwnerUserId == user.Id && item.Enabled)
            .Select(item => (long?)item.Revision).SingleOrDefaultAsync(token);
        if (revision == null) return card with { Songs = [], Readiness = new(false, "Your service is unavailable.") };
        var key = Hash(Scope(user) + ":" + revision + ":" + card.SourcePlaylistId);
        var saved = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user.Id && item.ProviderAccountId == card.ProviderAccountId &&
                item.ResourceKind == PreparedFeedKind && item.ExternalIdHash == key).OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
        if (saved == null) return card with { Songs = [], Readiness = new(false, "Preparing songs from your service…") };
        var prepared = JsonSerializer.Deserialize<PersonalMix>(saved.PayloadJson)!;
        var context = await ContextAsync(user, token);
        var blocked = await taste.BlockedSongsAsync(user.Id, prepared.Songs.Select(item => item.Song).ToArray(), token);
        var localIds = prepared.Songs.Where(item => item.Song.IsLocal).Select(item => item.Song.Id).ToArray();
        var allowed = localIds.Length == 0 ? [] : await LibraryTrackAccess.Query(db, context, await libraryAccess.ResolveAsync(context, token))
            .Where(item => localIds.Contains(item.BackendItemId)).Select(item => item.BackendItemId).ToArrayAsync(token);
        var songs = prepared.Songs.Where(item => !blocked.Contains(item.Song) && context.Policy!.Includes(item.Song) &&
            (!item.Song.IsLocal || allowed.Contains(item.Song.Id))).ToArray();
        return card with
        {
            Songs = songs,
            UpdatedAt = saved.RetrievedAt,
            SongCount = prepared.SongCount ?? card.SongCount,
            Evidence = prepared.Evidence is { } evidence ? evidence with
            { FromMusic = songs.Count(item => item.Song.IsLocal), FromServices = songs.Count(item => !item.Song.IsLocal), Shown = songs.Length } : null,
            Readiness = new(songs.Length > 0, songs.Length > 0 ? "Ready" : prepared.Songs.Count == 0 ? prepared.Readiness.Message : "No playable songs right now.")
        };
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
