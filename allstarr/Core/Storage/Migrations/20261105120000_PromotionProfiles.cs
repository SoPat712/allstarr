using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20261105120000_PromotionProfiles")]
public sealed class PromotionProfiles : LibraryPromotions
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "promotion_profiles",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_promotion_profiles", item => item.Key);
                table.ForeignKey("FK_promotion_profiles_users_OwnerUserId", item => item.OwnerUserId, "users", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_promotion_profiles_OwnerUserId", "promotion_profiles", "OwnerUserId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE promotion_profile_downgrade_guard (
                InUse INTEGER NOT NULL CONSTRAINT CK_promotion_profiles_requires_backup_restore CHECK(InUse=0));
            INSERT INTO promotion_profile_downgrade_guard SELECT EXISTS(SELECT 1 FROM promotion_profiles);
            DROP TABLE promotion_profile_downgrade_guard;
            """);
        migrationBuilder.DropTable("promotion_profiles");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        CollectionMigrationModel.ConfigureSnapshot(modelBuilder);
        PromotionProfileModel.ConfigureSnapshot(modelBuilder);
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
