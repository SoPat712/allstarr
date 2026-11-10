using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    private static readonly ConcurrentDictionary<string, long> FeedReadVersions = new(StringComparer.Ordinal);
    private bool _feedReadsChanged;
    internal long FeedReadRevision => FeedReadVersions.GetValueOrDefault(Database.GetConnectionString() ?? "");

    internal string FeedReadKey(ProtocolExecutionContext context, string scope) => "feed:read:v1:" +
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Database = Database.GetConnectionString(),
            Revision = FeedReadRevision,
            scope,
            context.Protocol,
            context.BackendInstanceId,
            context.VerifiedBackendPrincipalId,
            context.Actor,
            context.Policy
        })));

    private bool ChangesFeedReads() => ChangeTracker.Entries().Any(entry =>
        (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) &&
        (entry.Entity is UserRecord && (entry.State != EntityState.Modified || entry.Properties.Any(property => property.IsModified &&
             property.Metadata.Name is not (nameof(UserRecord.LastSeenAt) or nameof(UserRecord.UpdatedAt)))) ||
         entry.Entity is ProviderAccountRecord or RuntimeSettingRecord or TasteProfileRecord or
            SharedTagProfileRecord or LibraryTrackRecord or ProviderTrackIdentityRecord or CanonicalRecordingRecord or
            ExternalMetadataSnapshotRecord or PlaylistLinkRecord or PlaylistSourceSnapshotRecord or
            PlaylistSourceEntryRecord or PlaylistSyncRunRecord or PlaylistSyncEntryResultRecord or
            TrackMatchRecord or ManualTrackOverrideRecord or PlaylistTargetMembershipRecord));

    private void FeedReadsSaved(bool changed)
    {
        if (!changed) return;
        if (Database.CurrentTransaction != null) _feedReadsChanged = true;
        else InvalidateFeedReads();
    }

    internal void InvalidateFeedReads() => FeedReadVersions.AddOrUpdate(Database.GetConnectionString() ?? "", 1, (_, revision) => revision + 1);

    private sealed class FeedReadTransactions : DbTransactionInterceptor
    {
        internal static readonly FeedReadTransactions Instance = new();

        private static void Complete(TransactionEndEventData data, bool committed)
        {
            if (data.Context is not AllstarrDbContext db || !db._feedReadsChanged) return;
            if (committed) db.InvalidateFeedReads();
            db._feedReadsChanged = false;
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
