using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20261028200000_SharedTagProfiles")]
public sealed class SharedTagProfiles : LibraryPromotions
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "shared_tag_profiles",
        columns: table => new
        {
            Key = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
            PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
            RefreshedAt = table.Column<long>(type: "INTEGER", nullable: false),
            CapturedAt = table.Column<long>(type: "INTEGER", nullable: false)
        },
        constraints: table => table.PrimaryKey("PK_shared_tag_profiles", item => item.Key));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("shared_tag_profiles");

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
    }
}
