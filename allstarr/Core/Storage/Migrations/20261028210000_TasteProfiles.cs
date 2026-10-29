using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20261028210000_TasteProfiles")]
public sealed class TasteProfiles : LibraryPromotions
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "taste_profiles",
        columns: table => new
        {
            OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
            PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
            Revision = table.Column<long>(type: "INTEGER", nullable: false)
        },
        constraints: table =>
        {
            table.PrimaryKey("PK_taste_profiles", item => item.OwnerUserId);
            table.ForeignKey("FK_taste_profiles_users_OwnerUserId", item => item.OwnerUserId, "users", "Id", onDelete: ReferentialAction.Cascade);
        });

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE taste_downgrade_guard (
                InUse INTEGER NOT NULL CONSTRAINT CK_taste_profiles_requires_backup_restore CHECK(InUse=0));
            INSERT INTO taste_downgrade_guard SELECT EXISTS(SELECT 1 FROM taste_profiles);
            DROP TABLE taste_downgrade_guard;
            """);
        migrationBuilder.DropTable("taste_profiles");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
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
