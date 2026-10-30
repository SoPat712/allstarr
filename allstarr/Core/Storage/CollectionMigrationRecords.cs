using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class CollectionMigrationRunRecord
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string RequestKey { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public long Revision { get; set; }
}

public sealed class CollectionMigrationCollectionRecord
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string SourceId { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}

public static class CollectionMigrationModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CollectionMigrationRunRecord>(entity =>
        {
            entity.ToTable("collection_migration_runs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.RequestKey).HasMaxLength(200).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.OwnerUserId, item.RequestKey }).IsUnique();
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CollectionMigrationCollectionRecord>(entity =>
        {
            entity.ToTable("collection_migration_collections");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.SourceId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.HasIndex(item => new { item.RunId, item.SourceId }).IsUnique();
            entity.HasOne<CollectionMigrationRunRecord>().WithMany().HasForeignKey(item => item.RunId).OnDelete(DeleteBehavior.Restrict);
        });
    }
    public static void ConfigureSnapshot(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("allstarr.Core.Storage.CollectionMigrationRunRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("TEXT");
            entity.Property<Guid>("OwnerUserId").HasColumnType("TEXT");
            entity.Property<string>("RequestKey").IsRequired().HasMaxLength(200).HasColumnType("TEXT");
            entity.Property<string>("PayloadJson").IsRequired().HasColumnType("TEXT");
            entity.Property<long>("Revision").IsConcurrencyToken().HasColumnType("INTEGER");
            entity.HasKey("Id");
            entity.HasIndex("OwnerUserId", "RequestKey").IsUnique();
            entity.HasOne("allstarr.Core.Storage.UserRecord", null).WithMany().HasForeignKey("OwnerUserId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            entity.ToTable("collection_migration_runs");
        });
        modelBuilder.Entity("allstarr.Core.Storage.CollectionMigrationCollectionRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("TEXT");
            entity.Property<Guid>("RunId").HasColumnType("TEXT");
            entity.Property<string>("SourceId").IsRequired().HasMaxLength(200).HasColumnType("TEXT");
            entity.Property<string>("PayloadJson").IsRequired().HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("RunId", "SourceId").IsUnique();
            entity.HasOne("allstarr.Core.Storage.CollectionMigrationRunRecord", null).WithMany().HasForeignKey("RunId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            entity.ToTable("collection_migration_collections");
        });
    }

}
