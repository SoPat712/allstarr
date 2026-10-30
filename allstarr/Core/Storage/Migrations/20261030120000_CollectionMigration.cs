using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20261030120000_CollectionMigration")]
public sealed class CollectionMigration : LibraryPromotions
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE collection_migration_runs (
            Id TEXT NOT NULL PRIMARY KEY,
            OwnerUserId TEXT NOT NULL REFERENCES users(Id) ON DELETE RESTRICT,
            RequestKey TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            Revision INTEGER NOT NULL);
        CREATE UNIQUE INDEX IX_collection_migration_runs_OwnerUserId_RequestKey
            ON collection_migration_runs (OwnerUserId, RequestKey);
        CREATE TABLE collection_migration_collections (
            Id TEXT NOT NULL PRIMARY KEY,
            RunId TEXT NOT NULL REFERENCES collection_migration_runs(Id) ON DELETE RESTRICT,
            SourceId TEXT NOT NULL,
            PayloadJson TEXT NOT NULL);
        CREATE UNIQUE INDEX IX_collection_migration_collections_RunId_SourceId
            ON collection_migration_collections (RunId, SourceId);
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("collection_migration_collections");
        migrationBuilder.DropTable("collection_migration_runs");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        CollectionMigrationModel.ConfigureSnapshot(modelBuilder);
        modelBuilder.Entity("allstarr.Core.Storage.SharedTagProfileRecord", entity =>
        {
            entity.Property<string>("Key").HasMaxLength(400).HasColumnType("TEXT");
            entity.Property<string>("PayloadJson").IsRequired().HasColumnType("TEXT");
            entity.Property<long>("RefreshedAt").HasColumnType("INTEGER");
            entity.Property<long>("CapturedAt").HasColumnType("INTEGER");
            entity.HasKey("Key");
            entity.ToTable("shared_tag_profiles");
        });
        modelBuilder.Entity("allstarr.Core.Storage.TasteProfileRecord", entity =>
        {
            entity.Property<Guid>("OwnerUserId").ValueGeneratedNever().HasColumnType("TEXT");
            entity.Property<string>("PayloadJson").IsRequired().HasColumnType("TEXT");
            entity.Property<long>("Revision").IsConcurrencyToken().HasColumnType("INTEGER");
            entity.HasKey("OwnerUserId");
            entity.HasOne("allstarr.Core.Storage.UserRecord", null).WithMany().HasForeignKey("OwnerUserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            entity.ToTable("taste_profiles");
        });
    }
}
