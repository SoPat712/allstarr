using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    private static readonly ConcurrentDictionary<(string Database, Guid? Owner, string Model), long> FeedReadVersions = new();
    private readonly HashSet<(Guid? Owner, string Model)> _pendingFeedReads = [];
    private Guid? _feedReadTransaction;
    internal long FeedReadLibraryRevision => FeedReadVersion(null, "library");
    internal long FeedReadLibraryRevisionFor(Guid? owner) => FeedReadLibraryRevision + (owner == null ? 0 : FeedReadVersion(owner, "library"));

    private long FeedReadVersion(Guid? owner, string model) =>
        FeedReadVersions.GetValueOrDefault((Database.GetConnectionString() ?? "", owner, model));

    private static string FeedReadModel(string scope) => scope.StartsWith("library-", StringComparison.Ordinal) ? "library" :
        scope.StartsWith("mix", StringComparison.Ordinal) ? "mixes" :
        scope.StartsWith("releases", StringComparison.Ordinal) ? "releases" : "playlists";

    internal string FeedReadKey(ProtocolExecutionContext context, string scope, BackendLibraryAccess? access = null)
    {
        var owner = context.Actor?.EffectiveUserId;
        var model = FeedReadModel(scope);
        var policy = context.Policy;
        return "feed:read:v1:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Database = Database.GetConnectionString(),
            Revision = FeedReadVersion(owner, model),
            SharedRevision = owner == null ? 0 : FeedReadVersion(null, model),
            scope,
            context.Protocol,
            context.BackendInstanceId,
            context.VerifiedBackendPrincipalId,
            Listener = owner,
            ActorKind = context.Actor?.Kind,
            Policy = policy == null ? null : new
            {
                policy.AudioQuality,
                policy.LocalPreferenceWindow,
                policy.Preferences.ExplicitFilter,
                Labels = model == "playlists" ? new { policy.Preferences.ShowExternalLabel, policy.Preferences.ShowExplicitLabel } : null,
                Orders = policy.ProviderOrders.Where(item => item.Key != ProviderCapabilityKind.Lyrics)
                    .OrderBy(item => item.Key).Select(item => new { item.Key, item.Value }),
                Disabled = policy.DisabledProviders.Order(StringComparer.Ordinal),
                Modes = policy.ProviderModes.OrderBy(item => item.Key, StringComparer.Ordinal)
            },
            Access = access == null ? null : new { access.Succeeded, Libraries = access.LibraryIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal) }
        })));
    }

    private HashSet<(Guid? Owner, string Model)> ChangesFeedReads()
    {
        HashSet<(Guid? Owner, string Model)> changed = [];
        void All(Guid? owner) { changed.Add((owner, "mixes")); changed.Add((owner, "releases")); changed.Add((owner, "playlists")); changed.Add((owner, "library")); }
        void MixesAndPlaylists(Guid? owner) { changed.Add((owner, "mixes")); changed.Add((owner, "playlists")); }
        var snapshotOwners = new Dictionary<Guid, Guid?>();
        var linkOwners = new Dictionary<Guid, Guid?>();
        var runOwners = new Dictionary<Guid, Guid?>();
        Guid? SnapshotOwner(Guid id)
        {
            if (!snapshotOwners.TryGetValue(id, out var owner))
                snapshotOwners[id] = owner = PlaylistSourceSnapshots.Local.FirstOrDefault(item => item.Id == id)?.OwnerUserId ??
                    PlaylistSourceSnapshots.AsNoTracking().Where(item => item.Id == id).Select(item => (Guid?)item.OwnerUserId).SingleOrDefault();
            return owner;
        }
        Guid? LinkOwner(Guid id)
        {
            if (!linkOwners.TryGetValue(id, out var owner))
                linkOwners[id] = owner = PlaylistLinks.Local.FirstOrDefault(item => item.Id == id)?.OwnerUserId ??
                    PlaylistLinks.AsNoTracking().Where(item => item.Id == id).Select(item => (Guid?)item.OwnerUserId).SingleOrDefault();
            return owner;
        }
        Guid? RunOwner(Guid id)
        {
            if (!runOwners.TryGetValue(id, out var owner))
                runOwners[id] = owner = PlaylistSyncRuns.Local.FirstOrDefault(item => item.Id == id)?.OwnerUserId ??
                    PlaylistSyncRuns.AsNoTracking().Where(item => item.Id == id).Select(item => (Guid?)item.OwnerUserId).SingleOrDefault();
            return owner;
        }
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray())
        {
            switch (entry.Entity)
            {
                case UserRecord user when entry.State != EntityState.Modified || entry.Properties.Any(property => property.IsModified &&
                    property.Metadata.Name is not (nameof(UserRecord.LastSeenAt) or nameof(UserRecord.UpdatedAt))):
                    All(user.Id);
                    break;
                case ProviderAccountRecord account:
                    All(account.OwnerUserId);
                    break;
                case RuntimeSettingRecord setting:
                    var owner = setting.OwnerUserId;
                    if (owner == null && CleanOnlyLockKeys.IsLockKey(setting.Key))
                        owner = Guid.ParseExact(setting.Key[^32..], "N");
                    if (setting.Key == "Mixes:PublishToApps") MixesAndPlaylists(owner);
                    else if (setting.Key is ListeningPreferenceKeys.ShowExternalLabel or ListeningPreferenceKeys.ShowExplicitLabel)
                        changed.Add((owner, "playlists"));
                    else if (setting.Key is not (ListeningPreferenceKeys.LyricsOrder or ListeningPreferenceKeys.PlaybackStart or ListeningPreferenceKeys.HeartAction))
                        All(owner);
                    break;
                case TasteProfileRecord taste when entry.State != EntityState.Modified ||
                    TasteNudgesChanged(entry.OriginalValues.GetValue<string>(nameof(TasteProfileRecord.PayloadJson)), taste.PayloadJson):
                    MixesAndPlaylists(taste.OwnerUserId);
                    break;
                case ExternalMetadataSnapshotRecord snapshot:
                    switch (snapshot.ResourceKind)
                    {
                        case "personal-mixes":
                        case "service-feeds":
                        case "service-feed-songs":
                        case "service-feed-prepared":
                        case "playlist-fork":
                        case "playlist-membership":
                            MixesAndPlaylists(snapshot.OwnerUserId);
                            break;
                        case "release-follows":
                        case "release-dismissal":
                            changed.Add((snapshot.OwnerUserId, "releases"));
                            break;
                        case "release-catalog":
                            // Catalogs are shared by artist and provider account, regardless of who polled them.
                            changed.Add((null, "releases"));
                            break;
                        case "personal-mixes-preview":
                        case "release-rate":
                        case "library-artist-index":
                        case "library-artist-backfill":
                        case "library-artist-candidate":
                            break;
                        case "library-artist-fix":
                            changed.Add((null, "library"));
                            break;
                        default:
                            changed.Add((snapshot.OwnerUserId, "library"));
                            break;
                    }
                    break;
                case PlaylistLinkRecord link: MixesAndPlaylists(link.OwnerUserId); break;
                case PlaylistSourceSnapshotRecord playlistSnapshot: MixesAndPlaylists(playlistSnapshot.OwnerUserId); break;
                case PlaylistSourceEntryRecord source: MixesAndPlaylists(SnapshotOwner(source.PlaylistSourceSnapshotId)); break;
                case PlaylistSyncRunRecord run: MixesAndPlaylists(run.OwnerUserId); break;
                case PlaylistSyncEntryResultRecord result: MixesAndPlaylists(RunOwner(result.PlaylistSyncRunId)); break;
                case PlaylistTargetMembershipRecord member: MixesAndPlaylists(LinkOwner(member.PlaylistLinkId)); break;
                case ManualTrackOverrideRecord manual:
                    MixesAndPlaylists(manual.OwnerUserId);
                    changed.Add((manual.OwnerUserId, "library"));
                    break;
                case AuditEventRecord audit when audit.Action == "promotion.library-linked":
                    changed.Add((null, "library"));
                    break;
                case LibraryTrackRecord when entry.State == EntityState.Modified && !entry.Properties.Any(property => property.IsModified &&
                    property.Metadata.Name is not (nameof(LibraryTrackRecord.IndexedAt) or nameof(LibraryTrackRecord.UpdatedAt) or
                        nameof(LibraryTrackRecord.DurationRetrievedAt) or nameof(LibraryTrackRecord.Revision))):
                    break;
                case SharedTagProfileRecord or LibraryTrackRecord or ProviderTrackIdentityRecord or CanonicalRecordingRecord or TrackMatchRecord:
                    changed.Add((null, "library"));
                    break;
            }
        }
        return changed;
    }

    private static bool TasteNudgesChanged(string before, string after)
    {
        static string Nudges(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("Nudges", out var nudges) ? nudges.GetRawText() : "[]";
        }
        if (before == after) return false;
        try { return Nudges(before) != Nudges(after); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return true; }
    }

    private void FeedReadsSaved(HashSet<(Guid? Owner, string Model)> changed)
    {
        var transaction = Database.CurrentTransaction?.TransactionId;
        if (_feedReadTransaction != transaction) _pendingFeedReads.Clear();
        _feedReadTransaction = transaction;
        if (transaction != null) _pendingFeedReads.UnionWith(changed);
        else BumpFeedReads(changed);
    }

    private void BumpFeedReads(IEnumerable<(Guid? Owner, string Model)> changed)
    {
        var database = Database.GetConnectionString() ?? "";
        foreach (var (owner, model) in changed)
            FeedReadVersions.AddOrUpdate((database, owner, model), 1, (_, revision) => revision + 1);
    }

    internal void InvalidateFeedReads(Guid owner) => FeedReadsSaved([(owner, "mixes"), (owner, "playlists")]);

    private sealed class FeedReadTransactions : DbTransactionInterceptor
    {
        internal static readonly FeedReadTransactions Instance = new();

        private static void Complete(TransactionEndEventData data, bool committed)
        {
            if (data.Context is not AllstarrDbContext db) return;
            if (db._feedReadTransaction == data.TransactionId)
            {
                if (committed) db.BumpFeedReads(db._pendingFeedReads);
                db._pendingFeedReads.Clear();
                db._feedReadTransaction = null;
            }
            // Library writes can commit without any feed change, so they're published on their own.
            if (db._libraryReadsChanged)
            {
                if (committed) db.InvalidateLibraryReads();
                db._libraryReadsChanged = false;
            }
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Complete(eventData, true);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Complete(eventData, true);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Complete(eventData, false);
        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Complete(eventData, false);
            return Task.CompletedTask;
        }
    }
}
