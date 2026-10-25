using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public static class FavoriteLinkModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ProviderFavoriteLinkRecord>(entity =>
        {
            entity.ToTable("provider_favorite_links", table => table.HasCheckConstraint("CK_favorite_link_progress",
                "length(\"PairKey\")=64 AND length(\"UnionAdministratorFingerprint\")=64 AND \"ConfirmationVersion\"=1 AND \"UnionAttempt\">0 AND \"Phase\" BETWEEN 0 AND 2 AND \"ProcessedRows\">=0 AND \"SeededItems\">=0 AND \"Revision\">0 AND ((\"CursorAt\" IS NULL)=(\"CursorId\" IS NULL))"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.PairKey).HasMaxLength(64).IsRequired();
            entity.Property(item => item.UnionAdministratorFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => item.PairKey).IsUnique().HasDatabaseName("IX_provider_favorite_link_pair");
            entity.HasIndex(item => new { item.CreatedAt, item.Id }).HasDatabaseName("IX_provider_favorite_link_created");
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.ConfirmedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.UnionJobId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ProviderFavoriteLinkMemberRecord>(entity =>
        {
            entity.ToTable("provider_favorite_link_members", table => table.HasCheckConstraint("CK_favorite_link_member",
                "\"Protocol\" IN ('jellyfin','subsonic') AND length(\"BackendInstanceId\") BETWEEN 1 AND 200 AND length(\"BackendPrincipalId\") BETWEEN 1 AND 300 AND length(\"IdentityFingerprint\")=64 AND length(\"ConfirmedDisplayName\") BETWEEN 1 AND 200 AND ((\"UpperAt\" IS NULL)=(\"UpperId\" IS NULL))"));
            entity.HasKey(item => item.UserId);
            entity.Property(item => item.UserId).ValueGeneratedNever();
            entity.HasAlternateKey(item => new { item.UserId, item.LinkId });
            entity.Property(item => item.Protocol).HasMaxLength(32).IsRequired();
            entity.Property(item => item.BackendInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.BackendPrincipalId).HasMaxLength(300).IsRequired();
            entity.Property(item => item.IdentityFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ConfirmedDisplayName).HasMaxLength(200).IsRequired();
            entity.HasIndex(item => new { item.LinkId, item.Protocol }).IsUnique().HasDatabaseName("IX_provider_favorite_link_member_role");
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderFavoriteLinkRecord>().WithMany().HasForeignKey(item => item.LinkId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<LinkedProviderFavoriteStateRecord>(entity =>
        {
            entity.ToTable("linked_provider_favorite_states", table => table.HasCheckConstraint("CK_linked_provider_favorite_state",
                "length(\"ProviderId\") BETWEEN 1 AND 100 AND \"ResourceKind\" IN ('track','album','artist','playlist') AND length(\"ExternalId\") BETWEEN 1 AND 500 AND \"LifecycleVersion\">=0 AND \"Revision\">0"));
            entity.HasKey(item => new { item.LinkId, item.ProviderId, item.ResourceKind, item.ExternalId });
            entity.Property(item => item.ProviderId).HasMaxLength(100).UseCollation("BINARY");
            entity.Property(item => item.ResourceKind).HasMaxLength(32).UseCollation("BINARY");
            entity.Property(item => item.ExternalId).HasMaxLength(500).UseCollation("BINARY");
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasOne<ProviderFavoriteLinkRecord>().WithMany().HasForeignKey(item => item.LinkId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FavoriteEventRecord>().WithMany().HasForeignKey(item => item.LastIntentEventId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ProviderFavoriteLinkRequestRecord>(entity =>
        {
            entity.ToTable("provider_favorite_link_requests", table => table.HasCheckConstraint("CK_favorite_link_request",
                "\"Operation\" IN ('create','retry') AND length(\"RequestHash\")=64"));
            entity.HasKey(item => new { item.AdministratorUserId, item.RequestId });
            entity.Property(item => item.Operation).HasMaxLength(16).IsRequired();
            entity.Property(item => item.RequestHash).HasMaxLength(64).IsRequired();
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.AdministratorUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderFavoriteLinkRecord>().WithMany().HasForeignKey(item => item.LinkId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableJobRecord>().WithMany().HasForeignKey(item => item.JobId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<FavoriteEventRecord>(entity =>
        {
            entity.ToTable("favorite_events", table => table.HasCheckConstraint("CK_favorite_event_shared_scope",
                "((\"SharedLinkId\" IS NULL AND \"SharedLifecycleVersion\" IS NULL) OR (\"SharedLinkId\" IS NOT NULL AND \"SharedLifecycleVersion\" IS NOT NULL AND \"SharedLifecycleVersion\">=0 AND \"IsRealProviderIntent\"=1))"));
            entity.Property(item => item.IsRealProviderIntent).HasDefaultValue(false);
            entity.HasOne<ProviderFavoriteLinkMemberRecord>().WithMany().HasForeignKey(item => new { item.OwnerUserId, item.SharedLinkId })
                .HasPrincipalKey(item => new { item.UserId, item.LinkId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new
            {
                item.OwnerUserId,
                item.Protocol,
                item.BackendInstanceId,
                item.BackendPrincipalId,
                item.SharedLinkId,
                item.IsRealProviderIntent,
                item.CreatedAt,
                item.Id
            }).HasDatabaseName("IX_favorite_event_link_baseline");
            entity.HasIndex(item => new
            {
                item.OwnerUserId,
                item.Protocol,
                item.BackendInstanceId,
                item.BackendPrincipalId,
                item.ItemId,
                item.CreatedAt,
                item.Id
            }).HasDatabaseName("IX_favorite_event_link_item");
        });
    }
}
